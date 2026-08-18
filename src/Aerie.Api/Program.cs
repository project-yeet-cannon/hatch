using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Jobs;
using Aerie.Api.Models.Environment;
using Aerie.Api.Modules;
using Aerie.Api.Services;
using Aerie.Api.Services.ClimateControl;
using Aerie.Api.Services.Dashboard;
using Aerie.Api.Services.DeviceMapping;
using Aerie.Api.Services.Media;
using Aerie.Api.Services.Routines;
using HADotNet.Core;
using HADotNet.Core.Clients;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Quartz;
using System.Text.Json.Serialization;

////////
/// DI
var builder = WebApplication.CreateBuilder(args);

// Structured JSON console output outside local dev, so the fluent-bit ->
// OpenSearch pipeline (which tails raw container stdout, see
// containers/fluent-bit/fluent-bit.conf) can parse fields like State.Service
// out of each line instead of scraping human-formatted text. Left as the
// default Simple formatter in Development so `dotnet run` stays readable.
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.AddJsonConsole();
}

builder.Services.AddSingleton(TimeProvider.System);

// .env.json is optional now that ha_host/ha_port/ha_token live in SiteSettings
// (see DeviceMappingSeeder.SeedHomeAssistantConnectionAsync, which imports it
// once on first run if present). Kept around as a generic ISecrets source for
// local dev; production instead injects HA_HOST/HA_PORT/HA_TOKEN as environment
// variables (see compose.prod.yml), which the environment config provider below
// picks up under the same case-insensitive keys.
builder.Configuration.AddJsonFile(".env.json", optional: true);
builder.Services.AddSingleton<ISecrets>(new EnvSecrets(builder.Configuration));

// Pooled factory so services that fan out concurrent DB work (e.g.
// DashboardService's Task.WhenAll of zones + weather) can each create their
// own short-lived context instead of racing on one shared scoped instance,
// which throws "A second operation was started on this context..." under
// concurrent load. Scoped AerieContext is still available (resolved from the
// same pool) for services that only ever touch the DB sequentially.
builder.Services.AddPooledDbContextFactory<AerieContext>(o =>
{
    o.UseNpgsql(builder.Configuration.GetConnectionString("Aerie"));

    // EF Core logs SaveChangesFailed at Error level via its own diagnostics
    // source before the exception ever reaches a caller's catch block, so
    // ChannelHistoryWriter's handling of expected unique-key violations
    // (duplicate measurements/state changes) doesn't stop it from flooding
    // the logs. Downgrade it to Debug; genuine failures still throw and are
    // logged by the caller.
    o.ConfigureWarnings(w => w.Log((CoreEventId.SaveChangesFailed, LogLevel.Debug)));
});
builder.Services.AddScoped<AerieContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<AerieContext>>().CreateDbContext());

// Family app modules - separate schemas in the same database, each with its own
// DbContext and migration history. The registry lives in Modules/ so adding an
// app doesn't touch this file at all (see Modules/README.md).
builder.Services.AddAerieModules(builder.Configuration);

// Quartz
builder.Services.AddQuartz(q =>
{
    q.UsePersistentStore(sb =>
    {
        sb.UseProperties = true;
        sb.UseClustering();
        sb.UsePostgres(builder.Configuration.GetConnectionString("Quartz")!);
        sb.UseSystemTextJsonSerializer();
    });
});
builder.Services.AddQuartzHostedService(opt =>
{
    opt.WaitForJobsToComplete = true;
});
// AddQuartz only registers ISchedulerFactory; JobsInit and DevicesController
// need IScheduler directly, and GetScheduler() returns the same underlying
// instance AddQuartzHostedService starts, so this stays in sync with it.
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<ISchedulerFactory>().GetScheduler().GetAwaiter().GetResult());

// HADotNet - ClientFactory is static, process-local state (see
// HomeAssistantClientFactoryGate), so unlike the DB rows behind it, each api
// replica has to initialize its own. Every registration below runs the gate
// first; it no-ops after the first successful ApplyAsync on this process.
builder.Services.AddSingleton<HomeAssistantClientFactoryGate>();
AddHaClient<EntityClient>();
AddHaClient<HistoryClient>();
AddHaClient<StatesClient>();
AddHaClient<ServiceClient>();
AddHaClient<DiscoveryClient>();
AddHaClient<TemplateClient>();

void AddHaClient<TClient>() where TClient : BaseClient =>
    builder.Services.AddTransient(sp =>
    {
        sp.GetRequiredService<HomeAssistantClientFactoryGate>().EnsureInitialized(sp);
        return ClientFactory.GetClient<TClient>();
    });

// Services
builder.Services.AddTransient<IEnvironmentService, EnvironmentService>();
builder.Services.AddSingleton<IDocsService, DocsService>();

// Dashboard data services
builder.Services.AddSingleton<IForecastService, ForecastService>();
builder.Services.AddSingleton<ISiteSettingsService, SiteSettingsService>();
builder.Services.AddScoped<IZoneService, ZoneService>();
builder.Services.AddScoped<IRoutineService, RoutineService>();
builder.Services.AddTransient<IHomeAssistantStateReader, HomeAssistantStateReader>();
builder.Services.AddScoped<IWeatherService, WeatherService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IDeviceMappingSeeder, DeviceMappingSeeder>();
builder.Services.AddScoped<IDiscoveryService, DiscoveryService>();
builder.Services.AddScoped<IChannelHistoryWriter, ChannelHistoryWriter>();
builder.Services.AddScoped<IHomeAssistantConnectionManager, HomeAssistantConnectionManager>();
builder.Services.AddTransient<IHomeAssistantCommandService, HomeAssistantCommandService>();

// Every write to HA goes through IClimateCommandService, which ledgers it -
// nothing else should be resolving IHomeAssistantCommandService directly (see
// docs/climate-brain-architecture.md Phase 1).
builder.Services.AddScoped<IClimateCommandService, ClimateCommandService>();

// Jobs
builder.Services.AddTransient<IAerieJob, SampleChannels>();
builder.Services.AddTransient<IAerieJob, ReconcileCommands>();
builder.Services.AddTransient<BackfillChannelHistory>();
builder.Services.AddTransient<JobsInit>();

// Reaches the `files` peer (kiosk APK + signature checksum) - see
// KioskProvisioningController. "http://files/" is correct in both
// deployments today (the internal `edge` Docker network in compose.prod.yml,
// and the k8s Service named `files` in this pod's own namespace) but only by
// coincidence of both calling it "files"; KioskFiles:BaseAddress overrides it
// so a rename on either side doesn't become a silent 404.
var kioskFilesBaseAddress = builder.Configuration["KioskFiles:BaseAddress"] ?? "http://files/";
builder.Services.AddHttpClient("KioskFiles", c => c.BaseAddress = new Uri(kioskFilesBaseAddress));

builder.Services.Configure<MediaLibraryOptions>(builder.Configuration.GetSection(MediaLibraryOptions.SectionName));

// API / HTTP

// DataProtection: nothing here uses antiforgery tokens, cookie
// authentication, session state or TempData (grepped for all four, no
// matches), so there's no key ring that needs persisting across the three
// replicas. If any of that shows up later, its keys have to move to
// Postgres before replicas > 1 - otherwise each replica issues from its own
// ephemeral ring and can't read what another replica issued.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Aerie API",
        Version = "v1",
        Description = "[Open apps →](/apps/)"
    });
});
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

////////
/// Migrate mode - the chart's pre-install/pre-upgrade hook Job (5b.6) runs
/// this same image with AERIE_MIGRATE=1 instead of serving traffic, so the
/// migration provably runs the same code as the pods it precedes rather than
/// a second image that can drift from it. Runs once per deploy, ahead of any
/// replica; the serving pods below never take this branch.
if (builder.Configuration["AERIE_MIGRATE"] == "1")
{
    using var scope = app.Services.CreateScope();

    var db = scope.ServiceProvider.GetRequiredService<AerieContext>();
    await db.Database.MigrateAsync();

    // Every registered module context, in whatever order the registry lists them -
    // module schemas are independent by construction, so there is nothing to order.
    // A loop rather than a block per module is the point: module #2 never edits this.
    foreach (var moduleDb in scope.ServiceProvider.GetServices<IModuleContext>())
    {
        app.Logger.LogInformation("Migrating module context {Context}", moduleDb.GetType().Name);
        await moduleDb.Database.MigrateAsync();
    }

    var seeder = scope.ServiceProvider.GetRequiredService<IDeviceMappingSeeder>();
    await seeder.SeedAsync();

    var haConnection = scope.ServiceProvider.GetRequiredService<IHomeAssistantConnectionManager>();
    await haConnection.ApplyAsync(CancellationToken.None);

    return;
}

// Quartz
using (var scope = app.Services.CreateScope())
{
    var init = scope.ServiceProvider.GetRequiredService<JobsInit>();
    await init.WireUpJobs();
    await init.WireUpTriggerableJob<BackfillChannelHistory>(BackfillChannelHistory.Name, BackfillChannelHistory.Group);
}

// Graceful shutdown
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    var scheduler = app.Services.GetRequiredService<IScheduler>();
    if (scheduler.IsStarted)
        scheduler.Shutdown(waitForJobsToComplete: false).GetAwaiter().GetResult();
});

////////
/// HTTP Server
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// `api` is reached only via a reverse proxy with no port of its own exposed
// beyond that proxy - Caddy on the isolated `edge` Docker network (see
// docs/reverse-proxy-architecture.md) on the old host, Traefik's Ingress
// controller in the cluster (docs/plans/swarm/phase-5-app-tier.md) - so the
// immediate proxy is always trustworthy, but its container/pod IP is
// assigned at startup and can't be pinned as a KnownProxy. Clearing
// KnownNetworks/KnownProxies trusts X-Forwarded-For from whatever peer
// connects, which on either deployment is only ever that one proxy. Without
// this, RemoteIpAddress (used by UiLogsController/VmConsoleLogsController for
// actor telemetry) would just be the proxy's own container/pod IP for every
// request.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
// KnownIPNetworks/KnownProxies default to loopback only; Clear() (rather than
// an object-initializer collection, which would just add to those defaults)
// is what actually drops that restriction.
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

app.UseHttpsRedirection();

// Apps (static landing page + SPAs living under wwwroot/apps, outside the REST API)
var appsPath = Path.Combine(app.Environment.WebRootPath, "apps");
if (Directory.Exists(appsPath))
{
    var appsFiles = new PhysicalFileProvider(appsPath);
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = appsFiles,
        RequestPath = "/apps"
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = appsFiles,
        RequestPath = "/apps"
    });
}

// Media library (read-only music share, served so Sonos speakers can stream
// from it - see docs/media-library.md and DevicesController.PlayMedia). Off
// unless MediaLibrary:RootPath is configured; a configured-but-missing path is
// a deployment mistake worth a startup warning rather than silence, since the
// only other symptom is every play command 404ing at the speaker.
var mediaLibrary = app.Services.GetRequiredService<IOptions<MediaLibraryOptions>>().Value;
if (!string.IsNullOrWhiteSpace(mediaLibrary.RootPath))
{
    if (Directory.Exists(mediaLibrary.RootPath))
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(mediaLibrary.RootPath),
            RequestPath = mediaLibrary.RequestPath,
            ContentTypeProvider = MediaContentTypes.CreateProvider(),
        });
        app.Logger.LogInformation("Serving media library {RootPath} at {RequestPath}", mediaLibrary.RootPath, mediaLibrary.RequestPath);
    }
    else
    {
        app.Logger.LogWarning("MediaLibrary:RootPath {RootPath} does not exist - media library not served", mediaLibrary.RootPath);
    }
}

app.UseAuthorization();
app.MapControllers();
// Split so a database blip fails readiness (pod leaves the Service) without
// failing liveness (pod gets restarted) - restarting every replica over a
// dependency outage just turns one outage into a thundering-herd reconnect.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

// SPA fallback so client-side routes (e.g. /apps/admin/devices) survive a hard refresh.
// The :nonfile constraint excludes paths with a dot in the last segment (e.g.
// assets/index-abc123.js) so real static assets still resolve via UseStaticFiles above
// instead of being swallowed by this catch-all route during endpoint matching.
if (Directory.Exists(Path.Combine(appsPath, "admin")))
{
    app.MapFallbackToFile("/apps/admin/{*path:nonfile}", "apps/admin/index.html");
}
if (Directory.Exists(Path.Combine(appsPath, "docs")))
{
    app.MapFallbackToFile("/apps/docs/{*path:nonfile}", "apps/docs/index.html");
}
// The family shell needs this for more than refresh survival: a printed QR
// label encodes /apps/family/storage/c/{code} directly, so a cold scan from
// the stock camera app is *always* a deep link into a route that only exists
// client-side (see docs/family-apps-architecture.md).
if (Directory.Exists(Path.Combine(appsPath, "family")))
{
    app.MapFallbackToFile("/apps/family/{*path:nonfile}", "apps/family/index.html");
}

var opt = new RewriteOptions();
opt.AddRedirect("^$", "apps/");
opt.AddRedirect("^apps$", "apps/");
opt.AddRedirect("^apps/dashboard$", "apps/dashboard/");
opt.AddRedirect("^apps/admin$", "apps/admin/");
opt.AddRedirect("^apps/logo$", "apps/logo/");
opt.AddRedirect("^apps/modeler$", "apps/modeler/");
opt.AddRedirect("^apps/docs$", "apps/docs/");
opt.AddRedirect("^apps/family$", "apps/family/");
app.UseRewriter(opt);

app.UseSwagger();
app.UseSwaggerUI();
app.MapSwagger();

await app.RunAsync();

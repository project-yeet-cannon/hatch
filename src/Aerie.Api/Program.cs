using Aerie.Api.Common;
using Aerie.Api.Controllers;
using Aerie.Api.Ef;
using Aerie.Api.Jobs;
using Aerie.Api.Models.Environment;
using Aerie.Api.Modules;
using Aerie.Api.Services;
using Aerie.Api.Services.Auth;
using Aerie.Api.Services.Calendar;
using Aerie.Api.Services.ClimateControl;
using Aerie.Api.Services.Dashboard;
using Aerie.Api.Services.DeviceMapping;
using Aerie.Api.Services.Hazards;
using Aerie.Api.Services.Media;
using Aerie.Api.Services.Routines;
using HADotNet.Core;
using HADotNet.Core.Clients;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Quartz;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

////////
/// DI
var builder = WebApplication.CreateBuilder(args);

// Structured JSON console output outside local dev, so the fluent-bit ->
// OpenSearch pipeline (which tails raw container stdout, see
// deploy/cluster/observability/controllers/fluent-bit.yaml) can parse fields like State.Service
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
// Singleton so the parsed index.html is read once per replica rather than once
// per poll - every kiosk tablet hits this on a timer for as long as it's up.
builder.Services.AddSingleton<IAppVersionService, AppVersionService>();

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

// In-memory motion state, and the seam every reaction to motion hangs off.
// Singleton because the state is the process's, and because the WebSocket
// listener below and a kiosk's SSE connection have to be looking at the same
// one - see docs/plans/cameras.md Phase 5.
builder.Services.AddSingleton<IMotionEventDispatcher, MotionEventDispatcher>();

// One WebSocket subscription to HA's state_changed stream per api replica, not
// one per cluster - see HomeAssistantEventListener for why every replica needs
// its own. Never starts in the migrate Job: that branch returns before the host
// runs, so no hosted service in this file starts there.
builder.Services.AddHostedService<HomeAssistantEventListener>();

// Every write to HA goes through IClimateCommandService, which ledgers it -
// nothing else should be resolving IHomeAssistantCommandService directly (see
// docs/climate-brain-architecture.md Phase 1).
builder.Services.AddScoped<IClimateCommandService, ClimateCommandService>();

// Family calendar (docs/kiosk-architecture.md). One named client covers every
// host Google answers on - accounts.google.com and oauth2.googleapis.com for
// OAuth, www.googleapis.com for the Calendar API - so it carries no
// BaseAddress and the services call absolute URLs. The explicit timeout is the
// fail-soft convention: a slow Google leaves the calendar stale, it never
// stalls a request.
builder.Services.AddHttpClient(GoogleOAuthService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<IGoogleOAuthService, GoogleOAuthService>();
builder.Services.AddScoped<IGoogleTokenProvider, GoogleTokenProvider>();
builder.Services.AddScoped<IGoogleCalendarClient, GoogleCalendarClient>();
builder.Services.AddScoped<ICalendarDiscoveryService, CalendarDiscoveryService>();
builder.Services.AddScoped<ICalendarSyncService, CalendarSyncService>();
builder.Services.AddScoped<ICalendarAgendaService, CalendarAgendaService>();

// Outdoor hazards (docs/kiosk-architecture.md). Providers are registered
// against their interface rather than their own type: the resolver takes the
// whole IEnumerable and picks by Name, so adding a country's provider is one
// more line here and nothing else. Singletons because the resolver is one, and
// because a provider holds nothing per-request - only the client factory and
// the settings snapshot, which is itself a singleton.
//
// One 10-second client per half, each named for its role rather than its
// vendor so a second country's provider shares it. The explicit timeout is the
// fail-soft convention: a slow api.weather.gov leaves the hazard panel stale,
// it never stalls the sync job behind it.
builder.Services.AddHttpClient(NwsAlertProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient(OpenMeteoAirQualityProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<IWeatherAlertProvider, NwsAlertProvider>();
builder.Services.AddSingleton<IAirQualityProvider, OpenMeteoAirQualityProvider>();
builder.Services.AddSingleton<IHazardProviderResolver, HazardProviderResolver>();

// Scoped, unlike the providers: both of these hold a DbContext for the length
// of one sync or one request.
builder.Services.AddScoped<IHazardSyncService, HazardSyncService>();
builder.Services.AddScoped<IHazardService, HazardService>();

// Auth (docs/auth-architecture.md). AuthMiddleware and AuthController both run
// unconditionally, and both no-op or allow until Auth:Enabled becomes true -
// which is why false is the whole rollback.
var authSection = builder.Configuration.GetSection(AuthOptions.SectionName);
builder.Services.Configure<AuthOptions>(authSection);
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAuthGate, AuthGate>();

// Redemption is the only endpoint in the app that mints a credential, so it is
// the only one with a limiter. Bound once at startup rather than per request:
// AddPolicy's factory runs on the hot path, and the numbers are deploy-time
// config that cannot change without a restart anyway.
var authLimits = authSection.Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(AuthController.RedeemRateLimitPolicy, context =>
        // Partitioned by client IP, which UseForwardedHeaders has already
        // resolved from the proxy's X-Forwarded-For by the time this runs.
        //
        // Honest caveat: the limiter is per-process, so three replicas means
        // three times this budget. That is acceptable against a 40-bit code
        // behind a 15-minute TTL and single use, and it is not the primary
        // detector anyway - the Warning logged on every refused redemption is,
        // and those reach logs.<domain> from all three replicas alike.
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, authLimits.RedeemAttemptsPerWindow),
                Window = authLimits.RedeemWindow,
                // Queueing a guess to run later is not a kindness to anyone; a
                // 429 the sign-in shell can say out loud is.
                QueueLimit = 0,
            }));
});

// Jobs
builder.Services.AddTransient<IAerieJob, SampleChannels>();
builder.Services.AddTransient<IAerieJob, ReconcileCommands>();
builder.Services.AddTransient<IAerieJob, SyncCalendarEvents>();
builder.Services.AddTransient<IAerieJob, SyncOutdoorHazards>();
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
    // Module DTOs carry their module's name, so two modules can each have an
    // ItemDto without the document failing to generate - see SwaggerSchemaIds.
    c.CustomSchemaIds(SwaggerSchemaIds.For);
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

    // The chicken-and-egg: the thing that generates an invite lives behind the
    // wall. An install with no grant and no live invite has no way in at all,
    // so the migrate Job mints one - here rather than at replica startup,
    // because three replicas racing this would mint three invites, and this
    // branch runs exactly once per deploy ahead of any of them.
    //
    // This is the only place in the app a code is ever written to a log, and it
    // is correct here: it is reachable only when there is nothing to protect
    // the log from that isn't already reachable. On an install that has grants,
    // nothing below runs and nothing is printed.
    //
    // Deliberately not gated on Auth:Enabled, even though a wall that is off
    // needs no way through it. Gating it would put the one recovery path
    // behind a second piece of config reaching this Job correctly, and the
    // failure mode of getting that wrong - the wall goes up on an install that
    // minted nothing - is the unrecoverable one this exists to prevent. The
    // cost of being wrong the other way is one expiring row and one log line
    // per deploy on an install that has no wall.
    var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
    if (!await authService.HasAnyAccessAsync(CancellationToken.None))
    {
        var bootstrap = await authService.CreateInviteAsync("Bootstrap", isBootstrap: true, CancellationToken.None);
        app.Logger.LogWarning(
            "\n" +
            "========================================================\n" +
            " No enrolled devices and no live invite - minted one.\n" +
            " Sign in at /auth and enter:  {Code}\n" +
            " Valid until {ExpiresAt:u}. It can be used once.\n" +
            "========================================================",
            bootstrap.FormattedCode, bootstrap.ExpiresAt);
    }

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

// The wall. After UseForwardedHeaders because a refusal logs the client IP, and
// before the /apps static file handlers below because otherwise every SPA
// bundle serves to anyone who asks. No-ops entirely while Auth:Enabled is false
// (docs/auth-architecture.md).
app.UseMiddleware<AuthMiddleware>();

// Placed after the wall so an unauthenticated flood is refused before it can
// consume anyone's budget. Routing is added implicitly at the head of the
// pipeline by minimal hosting, so the [EnableRateLimiting] metadata on
// AuthController is already resolved by the time this runs.
app.UseRateLimiter();

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
        RequestPath = "/apps",
        // Without an explicit Cache-Control, StaticFileMiddleware sends only
        // ETag/Last-Modified, which leaves a browser free to apply *heuristic*
        // freshness and serve index.html from cache without revalidating. On
        // the kiosk tablets that turned a deploy into a no-op even across a
        // reload, because the stale index.html kept naming the stale bundle.
        //
        // Vite content-hashes everything under assets/ (index-BGJobmXl.js), so
        // those URLs are immutable by construction and can be cached hard. The
        // documents that *reference* them - index.html above all - must be
        // revalidated every time, which is what no-cache means (revalidate
        // before reuse), as opposed to no-store (never keep a copy at all): a
        // 304 on an unchanged index.html still costs nothing.
        OnPrepareResponse = ctx =>
        {
            var path = ctx.Context.Request.Path.Value ?? string.Empty;
            ctx.Context.Response.Headers.CacheControl =
                path.Contains("/assets/", StringComparison.Ordinal)
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
        },
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
// Same reasoning for the sign-in shell, twice over: /apps/auth/r/{code} is the
// URL a scanned invite QR resolves to, and it exists only client-side; and this
// is the page a gated request is redirected to, so a 404 here is a person
// locked out with nothing to read (docs/auth-architecture.md).
if (Directory.Exists(Path.Combine(appsPath, "auth")))
{
    app.MapFallbackToFile("/apps/auth/{*path:nonfile}", "apps/auth/index.html");
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
opt.AddRedirect("^apps/auth$", "apps/auth/");
// Short enough to read out over the phone to someone holding a new tablet -
// "go to home.<domain> slash auth" - which is the fallback that keeps the QR
// optional rather than required.
opt.AddRedirect("^auth/?$", "apps/auth/");
app.UseRewriter(opt);

app.UseSwagger();
app.UseSwaggerUI();
app.MapSwagger();

await app.RunAsync();

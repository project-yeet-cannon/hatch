using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Jobs;
using Aerie.Api.Models.Environment;
using Aerie.Api.Services;
using Aerie.Api.Services.Dashboard;
using Aerie.Api.Services.DeviceMapping;
using HADotNet.Core;
using HADotNet.Core.Clients;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi;
using Quartz;
using System.Text.Json.Serialization;

////////
/// DI
var builder = WebApplication.CreateBuilder(args);

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

// HADotNet - ClientFactory itself is initialized later, once SiteSettings is
// migrated/seeded and HomeAssistantConnectionManager can read it (see below).
// These registrations just wire up transient clients against whatever the
// factory is initialized to at request time.
builder.Services.AddTransient(_ => ClientFactory.GetClient<EntityClient>());
builder.Services.AddTransient(_ => ClientFactory.GetClient<HistoryClient>());
builder.Services.AddTransient(_ => ClientFactory.GetClient<StatesClient>());
builder.Services.AddTransient(_ => ClientFactory.GetClient<ServiceClient>());
builder.Services.AddTransient(_ => ClientFactory.GetClient<DiscoveryClient>());
builder.Services.AddTransient(_ => ClientFactory.GetClient<TemplateClient>());

// Services
builder.Services.AddTransient<IEnvironmentService, EnvironmentService>();

// Dashboard data services
builder.Services.AddSingleton<IForecastService, ForecastService>();
builder.Services.AddSingleton<ISiteSettingsService, SiteSettingsService>();
builder.Services.AddScoped<IZoneService, ZoneService>();
builder.Services.AddTransient<IHomeAssistantStateReader, HomeAssistantStateReader>();
builder.Services.AddScoped<IWeatherService, WeatherService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IDeviceMappingSeeder, DeviceMappingSeeder>();
builder.Services.AddScoped<IDiscoveryService, DiscoveryService>();
builder.Services.AddScoped<IChannelHistoryWriter, ChannelHistoryWriter>();
builder.Services.AddScoped<IHomeAssistantConnectionManager, HomeAssistantConnectionManager>();

// Jobs
builder.Services.AddTransient<IAerieJob, SampleChannels>();
builder.Services.AddTransient<BackfillChannelHistory>();
builder.Services.AddTransient<JobsInit>();

// API / HTTP
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
/// Migrations
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AerieContext>();
    await db.Database.MigrateAsync();

    var seeder = scope.ServiceProvider.GetRequiredService<IDeviceMappingSeeder>();
    await seeder.SeedAsync();

    var haConnection = scope.ServiceProvider.GetRequiredService<IHomeAssistantConnectionManager>();
    await haConnection.ApplyAsync(CancellationToken.None);
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

app.UseAuthorization();
app.MapControllers();

// SPA fallback so client-side routes (e.g. /apps/admin/devices) survive a hard refresh.
// The :nonfile constraint excludes paths with a dot in the last segment (e.g.
// assets/index-abc123.js) so real static assets still resolve via UseStaticFiles above
// instead of being swallowed by this catch-all route during endpoint matching.
if (Directory.Exists(Path.Combine(appsPath, "admin")))
{
    app.MapFallbackToFile("/apps/admin/{*path:nonfile}", "apps/admin/index.html");
}

var opt = new RewriteOptions();
opt.AddRedirect("^$", "swagger");
opt.AddRedirect("^apps$", "apps/");
opt.AddRedirect("^apps/dashboard$", "apps/dashboard/");
opt.AddRedirect("^apps/admin$", "apps/admin/");
app.UseRewriter(opt);

app.UseSwagger();
app.UseSwaggerUI();
app.MapSwagger();

await app.RunAsync();

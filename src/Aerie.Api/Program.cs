using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Jobs;
using Aerie.Api.Models.Environment;
using Aerie.Api.Services;
using Aerie.Api.Services.Dashboard;
using HADotNet.Core;
using HADotNet.Core.Clients;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi;
using Newtonsoft.Json;
using Quartz;

////////
/// DI
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);

var rawEnv = await File.ReadAllTextAsync(".env.json");
var env = JsonConvert.DeserializeObject<Dictionary<string, string>>(rawEnv);
var secrets = new EnvSecrets(env ?? []);
builder.Services.AddSingleton<ISecrets>(secrets);

// Pooled factory so services that fan out concurrent DB work (e.g.
// DashboardService's Task.WhenAll of zones + weather) can each create their
// own short-lived context instead of racing on one shared scoped instance,
// which throws "A second operation was started on this context..." under
// concurrent load. Scoped AerieContext is still available (resolved from the
// same pool) for services that only ever touch the DB sequentially.
builder.Services.AddPooledDbContextFactory<AerieContext>(o =>
    o.UseNpgsql(
        builder.Configuration.GetConnectionString("Aerie")));
builder.Services.AddScoped<AerieContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<AerieContext>>().CreateDbContext());

// Quartz
builder.Services.AddQuartz();
builder.Services.AddQuartzHostedService(opt =>
{
    opt.WaitForJobsToComplete = true;
});

// HADotNet
var haCfg = builder.Configuration.GetSection("HomeAssistant");
var haHost = secrets.GetSecret("ha_host");
var haPort = secrets.GetSecret("ha_port");
var haToken = secrets.GetSecret("ha_token");

ClientFactory.Initialize($"http://{haHost}:{haPort}/", haToken);

builder.Services.AddTransient(_ => ClientFactory.GetClient<EntityClient>());
builder.Services.AddTransient(_ => ClientFactory.GetClient<HistoryClient>());
builder.Services.AddTransient(_ => ClientFactory.GetClient<StatesClient>());
builder.Services.AddTransient(_ => ClientFactory.GetClient<ServiceClient>());
builder.Services.AddTransient(_ => ClientFactory.GetClient<DiscoveryClient>());

// Services
builder.Services.AddTransient<IEnvironmentService, EnvironmentService>();

// Dashboard data services
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection("Dashboard"));
builder.Services.AddSingleton<IForecastService, ForecastService>();
builder.Services.AddScoped<IZoneService, ZoneService>();
builder.Services.AddScoped<IWeatherService, WeatherService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();

// Jobs
builder.Services.AddTransient<IAerieJob, SampleEnvironments>();
builder.Services.AddTransient<IAerieJob, SampleOutside>();

var scheduler = await JobsInit.InitQuartz(
    builder.Configuration.GetConnectionString("Quartz")!);
builder.Services.AddSingleton(scheduler);
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
builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

////////
/// Migrations
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AerieContext>();
    await db.Database.MigrateAsync();
}

// Quartz
using (var scope = app.Services.CreateScope())
{
    var init = scope.ServiceProvider.GetRequiredService<JobsInit>();
    await init.WireUpJobs();
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

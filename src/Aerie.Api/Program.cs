using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Models.Environment;
using Aerie.Api.Services;
using HADotNet.Core;
using HADotNet.Core.Clients;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Newtonsoft.Json;

////////
/// DI
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);

var rawEnv = await File.ReadAllTextAsync(".env.json");
var env = JsonConvert.DeserializeObject<Dictionary<string, string>>(rawEnv);
var secrets = new EnvSecrets(env ?? []);
builder.Services.AddSingleton<ISecrets>(secrets);

builder.Services.AddDbContext<AerieContext>(o =>
    o.UseNpgsql(
        builder.Configuration.GetConnectionString("Aerie")));

// HADotNet
var haCfg = builder.Configuration.GetSection("HomeAssistant");
var haHost = secrets.GetSecret("ha_host");
var haPort = secrets.GetSecret("ha_port");
var haToken = secrets.GetSecret("ha_token");

Console.WriteLine($"http://{haHost}:{haPort}/");

ClientFactory.Initialize($"http://{haHost}:{haPort}/", haToken);

builder.Services.AddTransient(_ => ClientFactory.GetClient<EntityClient>());
builder.Services.AddTransient(_ => ClientFactory.GetClient<HistoryClient>());
builder.Services.AddTransient(_ => ClientFactory.GetClient<StatesClient>());
builder.Services.AddTransient(_ => ClientFactory.GetClient<ServiceClient>());
builder.Services.AddTransient(_ => ClientFactory.GetClient<DiscoveryClient>());

// Services
builder.Services.AddTransient<IEnvironmentService, EnvironmentService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

////////
/// Migrations
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AerieContext>();
    db.Database.Migrate();
}

////////
/// HTTP Server
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

var opt = new RewriteOptions();
opt.AddRedirect("^$", "swagger");
app.UseRewriter(opt);

app.UseSwagger();
app.UseSwaggerUI();
app.MapSwagger();

app.Run();

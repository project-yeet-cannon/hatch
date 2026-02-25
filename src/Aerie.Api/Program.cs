using Aerie.Api.Common;
using Aerie.Api.Ef;
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

var haCfg = builder.Configuration.GetSection("HomeAssistant");
var haHost = secrets.GetSecret("ha_host");
var haPort = secrets.GetSecret("ha_port");
var haToken = secrets.GetSecret("ha_token");

ClientFactory.Initialize($"http://{haHost}:{haPort}/", haToken);

builder.Services.AddScoped(_ => ClientFactory.GetClient<EntityClient>());
builder.Services.AddScoped(_ => ClientFactory.GetClient<HistoryClient>());
builder.Services.AddScoped(_ => ClientFactory.GetClient<StatesClient>());
builder.Services.AddScoped(_ => ClientFactory.GetClient<ServiceClient>());
builder.Services.AddScoped(_ => ClientFactory.GetClient<DiscoveryClient>());

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

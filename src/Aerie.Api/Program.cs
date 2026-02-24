using Aerie.Api.Ef;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Microsoft.AspNetCore.Rewrite;
using Aerie.Api.Common;

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

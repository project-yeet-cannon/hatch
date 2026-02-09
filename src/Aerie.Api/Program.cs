using HADotNet.Core;
using HADotNet.Core.Clients;
using Newtonsoft.Json;

// HA API POC

var rawEnv = await File.ReadAllTextAsync(".env.json");
var env = JsonConvert.DeserializeObject<Dictionary<string, string>>(rawEnv);

if (!env.TryGetValue("ha_key", out var haKey))
    throw new Exception("ha_key could not be found in .env.json file");

ClientFactory.Initialize("http://192.168.1.135:8123/", haKey);
// var client = ClientFactory.GetClient<EntityClient>();
// var x = await client.GetEntities();
// var z = x.ToList();
// Console.WriteLine(string.Join(",", z));


var client = ClientFactory.GetClient<HistoryClient>();
var history = await client.GetHistory("climate.mysa_1a4c98_thermostat_2", DateTimeOffset.Now.AddHours(-2), DateTimeOffset.Now);
foreach (var item in history)
{
    Console.WriteLine($"{item.LastChanged}  --  {item.Attributes["current_temperature"]}  --  {item.Attributes["current_humidity"]}");
}

Console.WriteLine(JsonConvert.SerializeObject(history));


// /HA API POC


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

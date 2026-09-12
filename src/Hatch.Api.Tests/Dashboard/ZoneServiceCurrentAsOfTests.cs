using Hatch.Api.Ef;
using Hatch.Api.Models.Dashboard;
using Hatch.Api.Services.Dashboard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Hatch.Api.Tests.Dashboard;

/// <summary>
/// CurrentAsOf is the only thing that lets a client tell a live house from a
/// dead one. CurrentTempF is the newest measurement anywhere in the 9-hour
/// history window, so a sensor that stopped reporting at noon still produces a
/// confident number at 5pm - and until this field existed, no client could have
/// known. See docs/kiosk-architecture.md ("How old is that number") and
/// lib/staleness.ts.
///
/// The distinction these tests exist to pin down is the one a later reader is
/// most likely to "fix": null when the value came from a bucket rather than a
/// row. A bucket boundary is not a moment anything was measured, and returning
/// it would make an interpolated value look fresher than the data under it.
/// </summary>
public class ZoneServiceCurrentAsOfTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CurrentAsOf_IsTheTimestampOfTheRowTheValueCameFrom()
    {
        var reading = Now.AddMinutes(-2);
        var zone = await SeedAsync((reading.AddMinutes(-30), 70m), (reading, 71.5m));

        Assert.Equal(71.5m, zone.CurrentTempF);
        Assert.Equal(reading, zone.CurrentAsOf);
    }

    [Fact]
    public async Task CurrentAsOf_IsOldWhenTheSensorWentQuiet_AndTheTemperatureStillReads()
    {
        // The failure this whole field exists for: five hours of silence inside
        // a nine-hour window, and CurrentTempF is as confident as ever.
        var lastHeardFrom = Now.AddHours(-5);
        var zone = await SeedAsync((lastHeardFrom, 68m));

        Assert.Equal(68m, zone.CurrentTempF);
        Assert.Equal(lastHeardFrom, zone.CurrentAsOf);
        Assert.True(Now - zone.CurrentAsOf!.Value > TimeSpan.FromMinutes(20));
    }

    [Fact]
    public async Task CurrentAsOf_IsNullWhenThereIsNoReadingAtAll()
    {
        var zone = await SeedAsync();

        Assert.Null(zone.CurrentTempF);
        Assert.Null(zone.CurrentAsOf);
    }

    [Fact]
    public async Task CurrentAsOf_IsNullForAReadingAtTheFarEdgeOfTheWindow()
    {
        // Outside the window entirely: the query drops it, so there is no row
        // and nothing to date. The zone reads as having no data rather than as
        // having stale data, which is the honest answer - the API cannot see
        // far enough back to say how old it is.
        var zone = await SeedAsync((Now.AddHours(-10), 65m));

        Assert.Null(zone.CurrentTempF);
        Assert.Null(zone.CurrentAsOf);
    }

    [Fact]
    public async Task CurrentAsOf_SurvivesTheRoundingAppliedToTheTemperature()
    {
        var reading = Now.AddMinutes(-1);
        var zone = await SeedAsync((reading, 70.06m));

        // The value is rounded to a tenth; its age is not rounded to anything.
        Assert.Equal(70.1m, zone.CurrentTempF);
        Assert.Equal(reading, zone.CurrentAsOf);
    }

    /// <summary>
    /// One included interior zone with a temperature channel, carrying the given
    /// measurements. Returns the ZoneClimate the dashboard would render.
    /// </summary>
    private static async Task<ZoneClimate> SeedAsync(params (DateTimeOffset At, decimal ValueF)[] measurements)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var zoneId = Guid.NewGuid();
        await using (var db = new AppDbContext(options))
        {
            var zone = new EfZone { Id = zoneId, Name = "Living Room", Kind = ZoneKind.Interior, Included = true };
            var device = new EfDevice { Name = "Living Room Thermostat", ZoneId = zoneId, Enabled = true };
            var channel = new EfDeviceChannel
            {
                DeviceId = device.Id,
                Device = device,
                Metric = DeviceChannelMetric.Temperature,
                HaEntityId = "sensor.living_room_temperature",
            };

            db.Zones.Add(zone);
            db.Devices.Add(device);
            db.DeviceChannels.Add(channel);
            foreach (var (at, value) in measurements)
            {
                db.Measurements.Add(new EfMeasurement { ChannelId = channel.Id, Timestamp = at, Value = value });
            }

            await db.SaveChangesAsync();
        }

        var service = new ZoneService(
            new TestDbContextFactory(options),
            new ForecastService(),
            new StubSiteSettings(),
            new FakeTimeProvider(Now));

        var zones = await service.GetZonesAsync(DashboardWindow.Default, CancellationToken.None);
        return Assert.Single(zones);
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}

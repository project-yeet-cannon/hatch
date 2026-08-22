using System.Net;
using Aerie.Api.Services.Hazards;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Hazards;

/// <summary>
/// Covers the two things the provider owes its caller - a current reading it
/// can trust and the worst hour still ahead - plus the Open-Meteo shapes that
/// would quietly corrupt both: offset-less timestamps, parallel arrays padded
/// with nulls, and a 200 that carries no index at all.
/// </summary>
public class OpenMeteoAirQualityProviderTests
{
    /// <summary>Mid-afternoon UTC, so the peak window spans both forecast days in the fixtures below.</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ParsesTheCurrentReadingWithItsComponents()
    {
        // Captured from air-quality-api.open-meteo.com, including the
        // offset-less timestamps that come back when timezone=UTC is asked
        // for - reading those as local time would shift every sample.
        var (provider, _) = NewProvider(Ok("""
            {"latitude":40.7,"longitude":-74.0,
             "current":{"time":"2026-08-21T16:00","interval":900,"us_aqi":118.0,"pm2_5":38.4,"pm10":44.1,"ozone":72.0,"nitrogen_dioxide":15.3},
             "hourly":{"time":["2026-08-21T16:00"],"us_aqi":[118.0]}}
            """));

        var reading = await provider.GetCurrentAsync(40.7, -74.0, CancellationToken.None);

        Assert.NotNull(reading);
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 16, 0, 0, TimeSpan.Zero), reading.Current.Timestamp);
        Assert.Equal(118, reading.Current.UsAqi);
        Assert.Equal(38.4m, reading.Current.Pm25);
        Assert.Equal(44.1m, reading.Current.Pm10);
        Assert.Equal(72.0m, reading.Current.Ozone);
        Assert.Equal(15.3m, reading.Current.No2);
    }

    [Fact]
    public async Task FindsTheWorstHourInTheComingDay()
    {
        var (provider, _) = NewProvider(Ok(Payload(
            currentAqi: 60,
            hours: [("2026-08-21T16:00", 60), ("2026-08-21T22:00", 143), ("2026-08-22T04:00", 90)])));

        var reading = await provider.GetCurrentAsync(40.7, -74.0, CancellationToken.None);

        // The evening spike, not the current hour - a clean afternoon ahead of
        // a smoky evening is the case a current reading hides, and the whole
        // reason the peak is fetched at all.
        Assert.Equal(143, reading!.Peak.UsAqi);
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 22, 0, 0, TimeSpan.Zero), reading.Peak.Timestamp);
    }

    [Fact]
    public async Task IgnoresAPeakBeyondTheComingDay()
    {
        var (provider, _) = NewProvider(Ok(Payload(
            currentAqi: 60,
            // Thirty hours out: real, and not what a glance at the wall today
            // is asking about.
            hours: [("2026-08-21T16:00", 60), ("2026-08-22T22:00", 180)])));

        var reading = await provider.GetCurrentAsync(40.7, -74.0, CancellationToken.None);

        Assert.Equal(60, reading!.Peak.UsAqi);
    }

    [Fact]
    public async Task ThePeakIsTheCurrentHourWhenTheForecastOnlyImproves()
    {
        var (provider, _) = NewProvider(Ok(Payload(
            currentAqi: 155,
            hours: [("2026-08-21T16:00", 155), ("2026-08-21T20:00", 80), ("2026-08-22T02:00", 40)])));

        var reading = await provider.GetCurrentAsync(40.7, -74.0, CancellationToken.None);

        Assert.Equal(155, reading!.Peak.UsAqi);
    }

    [Fact]
    public async Task DropsHoursWithNoIndexRatherThanReadingThemAsZero()
    {
        // Open-Meteo pads a series with nulls where it has no value. A zero
        // would be a clean-air reading that never happened - and would win the
        // "worst hour" comparison from the wrong end if the sign were ever
        // flipped.
        var (provider, _) = NewProvider(Ok("""
            {"current":{"time":"2026-08-21T16:00","us_aqi":60.0},
             "hourly":{"time":["2026-08-21T16:00","2026-08-21T17:00","2026-08-21T18:00"],"us_aqi":[60.0,null,75.0]}}
            """));

        var reading = await provider.GetCurrentAsync(40.7, -74.0, CancellationToken.None);

        Assert.Equal([60, 75], reading!.Hourly.Select(h => h.UsAqi));
    }

    [Fact]
    public async Task AResponseWithoutACurrentIndexIsNoReadingAtAll()
    {
        var (provider, _) = NewProvider(Ok("""{"hourly":{"time":["2026-08-21T16:00"],"us_aqi":[60.0]}}"""));

        Assert.Null(await provider.GetCurrentAsync(40.7, -74.0, CancellationToken.None));
    }

    [Fact]
    public async Task MalformedJsonReturnsNullRatherThanThrowing()
    {
        var (provider, _) = NewProvider(StubHttpMessageHandler.Json(HttpStatusCode.OK, "<html>not json</html>"));

        Assert.Null(await provider.GetCurrentAsync(40.7, -74.0, CancellationToken.None));
    }

    [Fact]
    public async Task AnErrorStatusReturnsNullRatherThanThrowing()
    {
        // Fail-soft, like every other outbound call here: a throttling
        // upstream leaves the panel stale, it never takes the sync down.
        var (provider, _) = NewProvider(StubHttpMessageHandler.Json(HttpStatusCode.TooManyRequests, "{}"));

        Assert.Null(await provider.GetCurrentAsync(40.7, -74.0, CancellationToken.None));
    }

    [Fact]
    public async Task AsksForThePointAndTheSeriesItNeeds()
    {
        var (provider, handler) = NewProvider(Ok(Payload(60, [("2026-08-21T16:00", 60)])));

        await provider.GetCurrentAsync(39.7392358, -104.990251, CancellationToken.None);

        var url = Uri.UnescapeDataString(Assert.Single(handler.Requests).Url);
        Assert.Contains("latitude=39.7392", url);
        Assert.Contains("longitude=-104.9903", url);
        Assert.Contains("current=us_aqi,pm2_5,pm10,ozone,nitrogen_dioxide", url);
        Assert.Contains("hourly=us_aqi", url);
        Assert.Contains("forecast_days=2", url);
        // The offset-less timestamps are only unambiguous because of this.
        Assert.Contains("timezone=UTC", url);
    }

    /// <summary>A well-formed response whose current hour and hourly series a test varies together, since the provider reads them as one picture.</summary>
    private static string Payload(int currentAqi, (string Time, int Aqi)[] hours) =>
        $$$"""
        {"current":{"time":"2026-08-21T16:00","us_aqi":{{{currentAqi}}}.0},
         "hourly":{"time":[{{{string.Join(",", hours.Select(h => $"\"{h.Time}\""))}}}],
                   "us_aqi":[{{{string.Join(",", hours.Select(h => $"{h.Aqi}.0"))}}}]}}
        """;

    private static HttpResponseMessage Ok(string body) => StubHttpMessageHandler.Json(HttpStatusCode.OK, body);

    private static (OpenMeteoAirQualityProvider Provider, StubHttpMessageHandler Handler) NewProvider(HttpResponseMessage response)
    {
        var handler = new StubHttpMessageHandler(response);
        return (new OpenMeteoAirQualityProvider(
            new StubHttpClientFactory(handler),
            new FakeTimeProvider(Now),
            NullLogger<OpenMeteoAirQualityProvider>.Instance), handler);
    }
}

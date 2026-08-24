using System.Text;
using Aerie.Api.Common;
using Aerie.Api.Services.Calendar;

namespace Aerie.Api.Tests.Calendar;

/// <summary>Hands out a fixed access token, or null to stand for an account whose grant is dead.</summary>
internal sealed class StubGoogleTokenProvider(string? accessToken = "access-token") : IGoogleTokenProvider
{
    public Task<string?> GetAccessTokenAsync(Guid accountId, CancellationToken ct) => Task.FromResult(accessToken);
}

/// <summary>
/// Answers with canned provider data instead of calling Google, so a test can
/// drive the discovery reconciliation - including the failure that must not be
/// mistaken for an empty account.
/// </summary>
internal sealed class StubGoogleCalendarClient(
    ProviderListResult<ProviderCalendar>? calendars = null,
    ProviderListResult<ProviderEvent>? events = null,
    Func<string, ProviderListResult<ProviderEvent>>? eventsFor = null) : IGoogleCalendarClient
{
    public List<Guid> CalendarListCalls { get; } = [];

    /// <summary>Every event fetch, so a sync test can assert which calendars were asked about and over what window.</summary>
    public List<(Guid AccountId, string CalendarId, DateTimeOffset TimeMin, DateTimeOffset TimeMax)> EventCalls { get; } = [];

    public Task<ProviderListResult<ProviderCalendar>> ListCalendarsAsync(Guid accountId, CancellationToken ct)
    {
        CalendarListCalls.Add(accountId);
        return Task.FromResult(calendars ?? ProviderListResult<ProviderCalendar>.Ok([]));
    }

    /// <summary>
    /// <paramref name="eventsFor"/> answers per calendar, which is how a sync
    /// test gives two calendars different days - or lets one of them throw,
    /// standing for the failure that must not cost the other account its
    /// agenda.
    /// </summary>
    public Task<ProviderListResult<ProviderEvent>> ListEventsAsync(
        Guid accountId, string providerCalendarId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct)
    {
        EventCalls.Add((accountId, providerCalendarId, timeMin, timeMax));
        return Task.FromResult(
            eventsFor?.Invoke(providerCalendarId) ?? events ?? ProviderListResult<ProviderEvent>.Ok([]));
    }
}

internal static class CalendarTestData
{
    /// <summary>An unsigned JWT with the given payload. Enough for EmailFromIdToken, which reads the payload and deliberately doesn't verify the signature.</summary>
    public static string IdToken(string payloadJson) =>
        $"{Base64Url("""{"alg":"none"}""")}.{Base64Url(payloadJson)}.signature";

    /// <summary>Obfuscates the way SettingsController and the OAuth callback do, so a seeded account looks like one Aerie wrote.</summary>
    public static string Stored(string plaintext) => SecretProtector.Protect(plaintext);

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace Hatch.Api.Services.Calendar;

/// <summary>
/// One calendar as a provider describes it, with the provider's own spelling
/// already normalized away - CalendarDiscoveryService writes these into
/// EfCalendar without knowing they came from Google.
/// </summary>
public record ProviderCalendar(string Id, string Name, string? Color, string? TimeZone, bool IsPrimary);

/// <summary>
/// One end of an event as the provider states it: either an instant (a timed
/// event, which carries its own UTC offset) or a bare calendar date (an all-day
/// event, which isn't at any particular instant until a timezone is applied).
/// Resolving the second into the first needs the site timezone, which is the
/// sync job's business and not this client's - so both forms come back intact.
/// </summary>
/// <param name="Date">For an all-day event. Google reports the *end* of one exclusively (a single-day event ends the following day), and that is passed through unchanged rather than quietly decremented here.</param>
public record ProviderEventTime(DateTimeOffset? Instant, DateOnly? Date);

/// <summary>One event instance. Recurring series arrive already expanded, so each occurrence is its own record with its own provider id.</summary>
public record ProviderEvent(string Id, string Title, string? Location, string? Status, ProviderEventTime Start, ProviderEventTime End);

/// <summary>
/// A list fetch's outcome. Items and an error are kept apart rather than
/// collapsed into an empty list because the difference matters downstream:
/// discovery deletes local calendars that no longer appear, and a fetch that
/// failed must not be read as "the account has no calendars any more".
/// </summary>
public record ProviderListResult<T>(IReadOnlyList<T> Items, string? Error)
{
    public bool Succeeded => Error is null;

    public static ProviderListResult<T> Ok(IReadOnlyList<T> items) => new(items, null);

    public static ProviderListResult<T> Failed(string error) => new([], error);
}

public interface IGoogleCalendarClient
{
    /// <summary>Every calendar the account can see, following the provider's paging to the end.</summary>
    Task<ProviderListResult<ProviderCalendar>> ListCalendarsAsync(Guid accountId, CancellationToken ct);

    /// <summary>Event instances overlapping [<paramref name="timeMin"/>, <paramref name="timeMax"/>), recurring series already expanded into occurrences.</summary>
    Task<ProviderListResult<ProviderEvent>> ListEventsAsync(
        Guid accountId, string providerCalendarId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct);
}

/// <summary>
/// The two Calendar v3 endpoints Hatch reads, called directly rather than
/// through Google.Apis.Calendar.v3 for the reason in docs/kiosk-architecture.md: the
/// SDK's value is a credential store, and Hatch's credentials live in Postgres.
///
/// Fail-soft like GoogleOAuthService and WeatherService: every path returns a
/// result. A dead or throttling Google leaves the calendar stale, which is the
/// intended degradation - it never throws out of a sync or a request.
/// </summary>
public class GoogleCalendarClient(
    IHttpClientFactory httpClientFactory,
    IGoogleTokenProvider tokens,
    ILogger<GoogleCalendarClient> logger) : IGoogleCalendarClient
{
    public const string CalendarListEndpoint = "https://www.googleapis.com/calendar/v3/users/me/calendarList";
    public const string CalendarsEndpoint = "https://www.googleapis.com/calendar/v3/calendars";

    /// <summary>Google's own maximum for both endpoints. Asking for it keeps a family-sized calendar to one round trip.</summary>
    private const int PageSize = 250;

    /// <summary>
    /// A stop on paging, not a cap on data: a page token that never advances
    /// would otherwise loop forever inside a request. At 250 per page this is
    /// far more than any household calendar reaches.
    /// </summary>
    private const int MaxPages = 40;

    /// <summary>Google's code for "the caller isn't allowed here" - a scope that was never granted, or a calendar the account lost access to.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>Google's code for a calendar that is gone - which an event fetch can hit for a calendar deleted since the last discovery run, while every other calendar on the account is still fine.</summary>
    public const string NotFound = "not_found";

    public Task<ProviderListResult<ProviderCalendar>> ListCalendarsAsync(Guid accountId, CancellationToken ct) =>
        ListAsync(accountId, CalendarListEndpoint, new Dictionary<string, string?>
        {
            ["maxResults"] = PageSize.ToString(),
            // The fields Hatch stores, named explicitly: a calendarList entry
            // otherwise carries reminder and notification settings that would be
            // fetched and discarded on every refresh.
            ["fields"] = "nextPageToken,items(id,summary,summaryOverride,backgroundColor,timeZone,primary)",
        }, ToCalendar, ct);

    public Task<ProviderListResult<ProviderEvent>> ListEventsAsync(
        Guid accountId, string providerCalendarId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct)
    {
        // The id is often an email address and can be an arbitrary provider
        // string, so it is a path *segment* that has to be escaped, not
        // concatenated.
        var url = $"{CalendarsEndpoint}/{Uri.EscapeDataString(providerCalendarId)}/events";

        return ListAsync(accountId, url, new Dictionary<string, string?>
        {
            // singleEvents=true is what makes Google expand a recurring series
            // server-side. Without it the response is the recurrence *rules*,
            // and Hatch would be in the business of evaluating RRULEs.
            ["singleEvents"] = "true",
            ["orderBy"] = "startTime",
            ["maxResults"] = PageSize.ToString(),
            ["timeMin"] = Rfc3339(timeMin),
            ["timeMax"] = Rfc3339(timeMax),
            ["fields"] = "nextPageToken,items(id,summary,location,status,start,end)",
        }, ToEvent, ct);
    }

    /// <summary>
    /// The shape both endpoints share: authorize once, then follow
    /// <c>nextPageToken</c> until it stops coming, mapping each page's items.
    /// Items that don't map (no id) are dropped rather than failing the page.
    /// </summary>
    private async Task<ProviderListResult<T>> ListAsync<T>(
        Guid accountId,
        string url,
        Dictionary<string, string?> query,
        Func<CalendarApiItem, T?> map,
        CancellationToken ct)
    {
        if (await tokens.GetAccessTokenAsync(accountId, ct) is not { } accessToken)
        {
            // GetAccessTokenAsync has already logged why and recorded it on the
            // account; this is the caller's cue that nothing was fetched.
            logger.LogWarning("No Google access token for account {AccountId}; skipping {Url}", accountId, url);
            return ProviderListResult<T>.Failed("no_access_token");
        }

        var client = httpClientFactory.CreateClient(GoogleOAuthService.HttpClientName);
        var items = new List<T>();
        string? pageToken = null;

        for (var page = 0; page < MaxPages; page++)
        {
            if (pageToken is not null) query["pageToken"] = pageToken;

            CalendarApiPage? body;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, QueryHelpers.AddQueryString(url, query));
                request.Headers.Authorization = new("Bearer", accessToken);

                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = ErrorFor(response.StatusCode);
                    logger.LogWarning(
                        "Google Calendar rejected {Url} for account {AccountId} with {Status} ({Error})",
                        url, accountId, (int)response.StatusCode, error);
                    return ProviderListResult<T>.Failed(error);
                }

                body = JsonSerializer.Deserialize<CalendarApiPage>(await response.Content.ReadAsStringAsync(ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Google Calendar request to {Url} could not be completed", url);
                return ProviderListResult<T>.Failed("unreachable");
            }

            foreach (var item in body?.Items ?? [])
                if (map(item) is { } mapped)
                    items.Add(mapped);

            pageToken = NullIfEmpty(body?.NextPageToken);
            if (pageToken is null) return ProviderListResult<T>.Ok(items);
        }

        // Every page so far was fetched successfully, so this is a real answer
        // that is merely incomplete - returning it beats discarding a calendar
        // that legitimately has thousands of entries.
        logger.LogWarning("Stopped paging {Url} for account {AccountId} after {Pages} pages", url, accountId, MaxPages);
        return ProviderListResult<T>.Ok(items);
    }

    /// <summary>Named codes for the statuses a caller branches on; everything else keeps its number, which is enough to put in LastSyncError.</summary>
    private static string ErrorFor(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.Forbidden => Forbidden,
        System.Net.HttpStatusCode.NotFound => NotFound,
        _ => $"http_{(int)status}",
    };

    private static ProviderCalendar? ToCalendar(CalendarApiItem item) =>
        NullIfEmpty(item.Id) is { } id
            // summaryOverride is the name the *account holder* gave a shared
            // calendar, which is the one they'd recognize in a list; summary is
            // the owner's.
            ? new ProviderCalendar(id, NullIfEmpty(item.SummaryOverride) ?? NullIfEmpty(item.Summary) ?? id,
                NullIfEmpty(item.BackgroundColor), NullIfEmpty(item.TimeZone), item.Primary ?? false)
            : null;

    private static ProviderEvent? ToEvent(CalendarApiItem item) =>
        NullIfEmpty(item.Id) is { } id && item.Start is { } start && item.End is { } end
            // An untitled event is legal in Google and shows there as "(No
            // title)"; giving it the same placeholder beats dropping it, since
            // the time slot is the part the family is reading.
            ? new ProviderEvent(id, NullIfEmpty(item.Summary) ?? "(No title)", NullIfEmpty(item.Location),
                NullIfEmpty(item.Status), ToEventTime(start), ToEventTime(end))
            : null;

    /// <summary>Google sends exactly one of <c>dateTime</c> (timed) or <c>date</c> (all-day); a malformed value becomes neither, which drops the event in ToEvent's caller.</summary>
    private static ProviderEventTime ToEventTime(CalendarApiTime time)
    {
        if (DateTimeOffset.TryParse(time.DateTime, out var instant)) return new ProviderEventTime(instant, null);
        if (DateOnly.TryParse(time.Date, out var date)) return new ProviderEventTime(null, date);
        return new ProviderEventTime(null, null);
    }

    /// <summary>Google requires RFC3339 with an offset, and rejects the fractional seconds "O" would emit.</summary>
    private static string Rfc3339(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private record CalendarApiPage(
        [property: JsonPropertyName("items")] List<CalendarApiItem>? Items,
        [property: JsonPropertyName("nextPageToken")] string? NextPageToken);

    /// <summary>
    /// One deserialization shape for both endpoints. calendarList entries and
    /// events overlap on id/summary and are disjoint everywhere else, so the
    /// unused half is simply absent - two near-identical records would cost
    /// more to read than the nullable fields do.
    /// </summary>
    private record CalendarApiItem(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("summaryOverride")] string? SummaryOverride,
        [property: JsonPropertyName("backgroundColor")] string? BackgroundColor,
        [property: JsonPropertyName("timeZone")] string? TimeZone,
        [property: JsonPropertyName("primary")] bool? Primary,
        [property: JsonPropertyName("location")] string? Location,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("start")] CalendarApiTime? Start,
        [property: JsonPropertyName("end")] CalendarApiTime? End);

    private record CalendarApiTime(
        [property: JsonPropertyName("dateTime")] string? DateTime,
        [property: JsonPropertyName("date")] string? Date);
}

using System.Net;
using Hatch.Api.Services.Calendar;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hatch.Api.Tests.Calendar;

/// <summary>
/// Covers the request Google actually receives - the parameters that change
/// what comes back, and the escaping a calendar id needs - plus the two
/// outcomes a caller branches on: a real empty list, and a fetch that failed.
/// </summary>
public class GoogleCalendarClientTests
{
    private static readonly DateTimeOffset TimeMin = new(2026, 8, 21, 4, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TimeMax = new(2026, 8, 23, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListCalendars_MapsTheFieldsHatchStores()
    {
        var (client, _) = NewClient(StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"items":[{"id":"family@example.com","summary":"Family","backgroundColor":"#4285f4","timeZone":"America/New_York","primary":true}]}
            """));

        var result = await client.ListCalendarsAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Succeeded);
        var calendar = Assert.Single(result.Items);
        Assert.Equal(new ProviderCalendar("family@example.com", "Family", "#4285f4", "America/New_York", true), calendar);
    }

    [Fact]
    public async Task ListCalendars_PrefersTheNameThisAccountGaveASharedCalendar()
    {
        var (client, _) = NewClient(StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"items":[{"id":"c1","summary":"Owner's name for it","summaryOverride":"What I call it"}]}
            """));

        var result = await client.ListCalendarsAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("What I call it", Assert.Single(result.Items).Name);
    }

    [Fact]
    public async Task ListCalendars_FollowsPagingToTheEnd()
    {
        var (client, handler) = NewClient(
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"items":[{"id":"c1","summary":"One"}],"nextPageToken":"page-2"}"""),
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"items":[{"id":"c2","summary":"Two"}]}"""));

        var result = await client.ListCalendarsAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(["c1", "c2"], result.Items.Select(c => c.Id));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("pageToken=page-2", handler.Requests[1].Url);
    }

    [Fact]
    public async Task ListCalendars_AnEmptyAccountIsASuccess_NotAFailure()
    {
        var (client, _) = NewClient(StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"items":[]}"""));

        var result = await client.ListCalendarsAsync(Guid.NewGuid(), CancellationToken.None);

        // Discovery deletes calendars that no longer appear, so this has to be
        // distinguishable from the failures below.
        Assert.True(result.Succeeded);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ARejectedRequestFails_RatherThanLookingLikeAnEmptyAccount()
    {
        var (client, _) = NewClient(StubHttpMessageHandler.Json(HttpStatusCode.Forbidden, """{"error":{"code":403}}"""));

        var result = await client.ListCalendarsAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(GoogleCalendarClient.Forbidden, result.Error);
    }

    [Fact]
    public async Task NoUsableToken_FailsWithoutCallingGoogle()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = new GoogleCalendarClient(
            new StubHttpClientFactory(handler), new StubGoogleTokenProvider(null), NullLogger<GoogleCalendarClient>.Instance);

        var result = await client.ListCalendarsAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("no_access_token", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListEvents_AsksForExpandedOccurrencesInTheGivenWindow()
    {
        var (client, handler) = NewClient(StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"items":[]}"""));

        await client.ListEventsAsync(Guid.NewGuid(), "family@example.com", TimeMin, TimeMax, CancellationToken.None);

        var url = Assert.Single(handler.Requests).Url;
        // Without singleEvents the response is recurrence rules, not instances.
        Assert.Contains("singleEvents=true", url);
        Assert.Contains("orderBy=startTime", url);
        Assert.Contains("timeMin=2026-08-21T04%3A00%3A00Z", url);
        Assert.Contains("timeMax=2026-08-23T04%3A00%3A00Z", url);
    }

    [Fact]
    public async Task ListEvents_EscapesTheCalendarIdIntoThePath()
    {
        var (client, handler) = NewClient(StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"items":[]}"""));

        await client.ListEventsAsync(Guid.NewGuid(), "en.usa#holiday@group.v.calendar.google.com", TimeMin, TimeMax, CancellationToken.None);

        // A raw '#' would truncate the URL at the fragment and fetch the wrong
        // calendar entirely.
        Assert.Contains("en.usa%23holiday%40group.v.calendar.google.com/events", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task ListEvents_KeepsTimedAndAllDayEventsApart()
    {
        var (client, _) = NewClient(StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"items":[
              {"id":"timed","summary":"Dentist","location":"Main St","status":"confirmed",
               "start":{"dateTime":"2026-08-21T09:00:00-04:00"},"end":{"dateTime":"2026-08-21T10:00:00-04:00"}},
              {"id":"allday","summary":"Trip","start":{"date":"2026-08-22"},"end":{"date":"2026-08-23"}}
            ]}
            """));

        var result = await client.ListEventsAsync(Guid.NewGuid(), "c1", TimeMin, TimeMax, CancellationToken.None);

        var timed = result.Items.Single(e => e.Id == "timed");
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 9, 0, 0, TimeSpan.FromHours(-4)), timed.Start.Instant);
        Assert.Null(timed.Start.Date);
        Assert.Equal("Main St", timed.Location);

        // The all-day end stays as Google's exclusive date; resolving it into an
        // instant needs the site timezone and belongs to the sync job.
        var allDay = result.Items.Single(e => e.Id == "allday");
        Assert.Null(allDay.Start.Instant);
        Assert.Equal(new DateOnly(2026, 8, 22), allDay.Start.Date);
        Assert.Equal(new DateOnly(2026, 8, 23), allDay.End.Date);
    }

    [Fact]
    public async Task ListEvents_KeepsAnUntitledEventRatherThanDroppingItsTimeSlot()
    {
        var (client, _) = NewClient(StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"items":[{"id":"e1","start":{"date":"2026-08-22"},"end":{"date":"2026-08-23"}}]}
            """));

        var result = await client.ListEventsAsync(Guid.NewGuid(), "c1", TimeMin, TimeMax, CancellationToken.None);

        Assert.Equal("(No title)", Assert.Single(result.Items).Title);
    }

    [Fact]
    public async Task AnUnusableItemIsDropped_WithoutFailingThePage()
    {
        var (client, _) = NewClient(StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"items":[
              {"summary":"No id at all","start":{"date":"2026-08-22"},"end":{"date":"2026-08-23"}},
              {"id":"e2","summary":"Fine","start":{"date":"2026-08-22"},"end":{"date":"2026-08-23"}}
            ]}
            """));

        var result = await client.ListEventsAsync(Guid.NewGuid(), "c1", TimeMin, TimeMax, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("e2", Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task RequestsCarryTheAccessTokenAsABearerToken()
    {
        var (client, handler) = NewClient(StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"items":[]}"""));

        await client.ListCalendarsAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("Bearer access-token", Assert.Single(handler.AuthorizationHeaders));
    }

    private static (GoogleCalendarClient Client, StubHttpMessageHandler Handler) NewClient(params HttpResponseMessage[] responses)
    {
        var handler = new StubHttpMessageHandler(responses);
        return (new GoogleCalendarClient(
            new StubHttpClientFactory(handler), new StubGoogleTokenProvider(), NullLogger<GoogleCalendarClient>.Instance), handler);
    }
}

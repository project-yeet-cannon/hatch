using System.Text.Json;
using Aerie.Api.Ef;
using Aerie.Api.Modules.Hatch;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// Hatch's API, and mostly the three properties that are expensive to get wrong
/// later: an issue key means one issue forever, a hierarchy stays a tree, and
/// every mutation leaves exactly one honest line in the audit trail.
/// </summary>
public class IssuesControllerTests
{
    // ---- Numbering ----

    [Fact]
    public async Task IssueNumbers_CountUpWithinAProject()
    {
        var h = await NewAsync();

        var first = await h.CreateAsync("task", "the first thing");
        var second = await h.CreateAsync("task", "the second thing");

        Assert.Equal("AER-1", first.Key);
        Assert.Equal("AER-2", second.Key);
    }

    [Fact]
    public async Task TwoProjects_NumberThemselvesIndependently()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "aerie work");

        var other = await h.CreateAsync("task", "ops work", projectId: h.OtherProjectId);

        Assert.Equal("OPS-1", other.Key);
    }

    /// <summary>
    /// A number is never handed back. <c>AER-12</c> in an old chat log has to
    /// be a dead link rather than a different ticket, which is why the counter
    /// is a column and not <c>MAX(Number) + 1</c>.
    /// </summary>
    [Fact]
    public async Task ADeletedIssuesNumber_IsNotReused()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "doomed");
        await h.Issues.DeleteIssue("AER-1", default);

        var next = await h.CreateAsync("task", "after it");

        Assert.Equal("AER-2", next.Key);
    }

    [Fact]
    public async Task ANewIssue_LandsInTheLeftmostColumnAtTheBottom()
    {
        var h = await NewAsync();
        var first = await h.CreateAsync("task", "first");

        var second = await h.CreateAsync("task", "second");

        Assert.Equal(h.Inbox, first.StatusId);
        Assert.Equal(h.Inbox, second.StatusId);
        Assert.True(second.Rank > first.Rank);
    }

    // ---- Resolving a key ----

    [Fact]
    public async Task AnIssueKey_ResolvesWhateverCaseItIsTypedIn()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        Assert.Equal("the thing", Value(await h.Issues.GetIssue("AER-1", default)).Title);
        Assert.Equal("the thing", Value(await h.Issues.GetIssue("aer-1", default)).Title);
    }

    /// <summary>
    /// Every way of not naming an issue is the same 404 - a number nobody
    /// minted, a project that does not exist, and a string that is not a key at
    /// all. There is nothing worth telling apart between them.
    /// </summary>
    [Theory]
    [InlineData("AER-999")]
    [InlineData("ZZZ-1")]
    [InlineData("AER")]
    [InlineData("AER-")]
    [InlineData("AER-0")]
    [InlineData("AER-x")]
    [InlineData("-1")]
    public async Task AKeyThatNamesNothing_IsA404(string key)
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        Assert.IsType<NotFoundResult>((await h.Issues.GetIssue(key, default)).Result);
    }

    // ---- Parenting ----

    [Fact]
    public async Task AStory_HangsUnderAnEpic()
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "the plan");

        var story = await h.CreateAsync("story", "phase 0", parentKey: "AER-1");

        Assert.Equal("AER-1", story.ParentKey);
        Assert.Equal(["AER-2"], Value(await h.Issues.GetIssue("AER-1", default)).ChildKeys);
    }

    [Fact]
    public async Task AParentInAnotherProject_IsRefused()
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "ops plan", projectId: h.OtherProjectId);

        var result = await h.Issues.CreateIssue(
            new IssueCreateRequest(h.ProjectId, "story", "phase 0", null, "OPS-1", null, null), default);

        Assert.Contains("another project", Reason(result.Result));
    }

    [Fact]
    public async Task AParentOfTheWrongType_IsRefused()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "a chore");

        var result = await h.Issues.CreateIssue(
            new IssueCreateRequest(h.ProjectId, "story", "phase 0", null, "AER-1", null, null), default);

        Assert.Contains("hangs under", Reason(result.Result));
    }

    [Fact]
    public async Task AParentThatDoesNotExist_IsRefused()
    {
        var h = await NewAsync();

        var result = await h.Issues.CreateIssue(
            new IssueCreateRequest(h.ProjectId, "story", "phase 0", null, "AER-99", null, null), default);

        Assert.Contains("no AER-99", Reason(result.Result));
    }

    /// <summary>
    /// A loop is not merely untidy: the detail page follows parents upward, and
    /// a cycle is a page that never finishes.
    /// </summary>
    [Fact]
    public async Task AParentThatIsAlreadyBelowTheIssue_IsRefused()
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "outer");
        await h.CreateAsync("epic", "inner", parentKey: "AER-1");

        var result = await h.Issues.PatchIssue("AER-1", Patch(parentKey: "AER-2"), default);

        Assert.Contains("already below", Reason(result.Result));
        Assert.Null(Value(await h.Issues.GetIssue("AER-1", default)).ParentKey);
    }

    [Fact]
    public async Task AnIssue_CannotBeItsOwnParent()
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "outer");

        var result = await h.Issues.PatchIssue("AER-1", Patch(parentKey: "AER-1"), default);

        Assert.Contains("its own parent", Reason(result.Result));
    }

    /// <summary>
    /// A JSON body cannot otherwise tell "no opinion" from "no parent", so the
    /// empty string is the clear - and an absent field has to leave the parent
    /// exactly where it was.
    /// </summary>
    [Fact]
    public async Task AnEmptyParentKeyClearsTheParent_AndAnAbsentOneLeavesIt()
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "the plan");
        await h.CreateAsync("story", "phase 0", parentKey: "AER-1");

        await h.Issues.PatchIssue("AER-2", Patch(title: "phase zero"), default);
        Assert.Equal("AER-1", Value(await h.Issues.GetIssue("AER-2", default)).ParentKey);

        await h.Issues.PatchIssue("AER-2", Patch(parentKey: ""), default);
        Assert.Null(Value(await h.Issues.GetIssue("AER-2", default)).ParentKey);
    }

    // ---- Events ----

    [Fact]
    public async Task Creating_WritesOneCreatedEvent()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        var events = await h.EventsAsync("AER-1");

        Assert.Equal([EfHatchIssueEvent.Created], events.Select(e => e.Kind));
        Assert.Equal("Nathan", events[0].Actor);
    }

    [Theory]
    [InlineData(nameof(IssuePatchRequest.Title), EfHatchIssueEvent.Retitled)]
    [InlineData(nameof(IssuePatchRequest.Description), EfHatchIssueEvent.Redescribed)]
    [InlineData(nameof(IssuePatchRequest.Type), EfHatchIssueEvent.Retyped)]
    [InlineData(nameof(IssuePatchRequest.StatusId), EfHatchIssueEvent.StatusChanged)]
    [InlineData(nameof(IssuePatchRequest.ParentKey), EfHatchIssueEvent.ParentChanged)]
    public async Task EachChangedField_WritesItsOwnEvent(string field, string kind)
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "the plan");
        await h.CreateAsync("story", "the thing");

        var patch = field switch
        {
            nameof(IssuePatchRequest.Title) => Patch(title: "something else"),
            nameof(IssuePatchRequest.Description) => Patch(description: "now with detail"),
            nameof(IssuePatchRequest.Type) => Patch(type: "bug"),
            nameof(IssuePatchRequest.StatusId) => Patch(statusId: h.Done),
            _ => Patch(parentKey: "AER-1"),
        };

        await h.Issues.PatchIssue("AER-2", patch, default);

        var events = await h.EventsAsync("AER-2");
        Assert.Equal([kind, EfHatchIssueEvent.Created], events.Select(e => e.Kind));
    }

    [Fact]
    public async Task AnEventsPayload_SaysWhatItChangedFromAndTo()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        await h.Issues.PatchIssue("AER-1", Patch(title: "the other thing"), default);

        var payload = (await h.EventsAsync("AER-1"))[0].Payload!.Value;
        Assert.Equal("the thing", payload.GetProperty("from").GetString());
        Assert.Equal("the other thing", payload.GetProperty("to").GetString());
    }

    /// <summary>
    /// A status event names the columns rather than their ids. Ids stop meaning
    /// anything the moment a column is deleted, and the trail is read long
    /// after.
    /// </summary>
    [Fact]
    public async Task AStatusEvent_NamesTheColumns()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        await h.Issues.MoveIssue("AER-1", new IssueMoveRequest(h.Done, null, null), default);

        var payload = (await h.EventsAsync("AER-1"))[0].Payload!.Value;
        Assert.Equal("inbox", payload.GetProperty("from").GetString());
        Assert.Equal("done", payload.GetProperty("to").GetString());
    }

    [Fact]
    public async Task AFieldResentUnchanged_WritesNothing()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        await h.Issues.PatchIssue("AER-1", Patch(title: "the thing", type: "story"), default);

        Assert.Single(await h.EventsAsync("AER-1"));
    }

    [Fact]
    public async Task SeveralFieldsAtOnce_WriteOneEventEach()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        await h.Issues.PatchIssue("AER-1", Patch(title: "renamed", type: "bug", description: "why"), default);

        var kinds = (await h.EventsAsync("AER-1")).Select(e => e.Kind).ToList();
        Assert.Equal(3, kinds.Count(k => k != EfHatchIssueEvent.Created));
    }

    // ---- Ready and due ----

    [Fact]
    public async Task AnIssue_CarriesBothDatesBack()
    {
        var h = await NewAsync();

        var created = await h.CreateAsync("task", "renew the cert", readyAt: "2027-08-15", dueAt: "2027-09-01T17:00:00Z");

        Assert.Equal("2027-08-15", created.ReadyAt);
        Assert.Equal("2027-09-01T17:00:00Z", created.DueAt);
    }

    [Fact]
    public async Task AnIssueWithNoDates_HasNeither()
    {
        var h = await NewAsync();

        var created = await h.CreateAsync("task", "some day");

        Assert.Null(created.ReadyAt);
        Assert.Null(created.DueAt);
    }

    /// <summary>
    /// Half of what a tracker is for is recording that something was due last
    /// Tuesday. A form that argues about it is one people stop telling the
    /// truth to.
    /// </summary>
    [Fact]
    public async Task ADateInThePast_IsAccepted()
    {
        var h = await NewAsync();

        var created = await h.CreateAsync("bug", "this was overdue", dueAt: "2020-01-01");

        Assert.Equal("2020-01-01", created.DueAt);
    }

    /// <summary>
    /// A ready date after a due date is a mix-up worth seeing on the card, not
    /// one worth refusing an edit for. Nothing here checks one against the
    /// other.
    /// </summary>
    [Fact]
    public async Task AnIssueReadyAfterItIsDue_IsNotArguedWith()
    {
        var h = await NewAsync();

        var created = await h.CreateAsync("task", "backwards", readyAt: "2027-09-01", dueAt: "2027-08-15");

        Assert.Equal("2027-09-01", created.ReadyAt);
        Assert.Equal("2027-08-15", created.DueAt);
    }

    [Theory]
    [InlineData("tomorrow", "readyAt")]
    [InlineData("2026-13-45", "readyAt")]
    public async Task AReadyDateThatIsNotADate_IsRefusedWithAReason(string text, string field)
    {
        var h = await NewAsync();

        var result = await h.Issues.CreateIssue(
            new IssueCreateRequest(h.ProjectId, "task", "when?", null, null, text, null), default);

        Assert.Contains(field, Reason(result.Result));
        Assert.Contains("2026-09-12", Reason(result.Result));
    }

    [Fact]
    public async Task ADueDateThatIsNotADate_IsRefusedAndNamesTheFieldItCameFrom()
    {
        var h = await NewAsync();

        var result = await h.Issues.CreateIssue(
            new IssueCreateRequest(h.ProjectId, "task", "when?", null, null, null, "soon"), default);

        Assert.Contains("dueAt", Reason(result.Result));
    }

    [Fact]
    public async Task SettingADate_WritesOneEventNamingBothEnds()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "renew the cert");

        await h.Issues.PatchIssue("AER-1", Patch(dueAt: "2027-09-01"), default);

        var events = await h.EventsAsync("AER-1");
        Assert.Equal([EfHatchIssueEvent.DueChanged, EfHatchIssueEvent.Created], events.Select(e => e.Kind));

        var payload = events[0].Payload!.Value;
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("from").ValueKind);
        Assert.Equal("2027-09-01", payload.GetProperty("to").GetString());
    }

    [Fact]
    public async Task MovingBothDatesAtOnce_WritesOneEventEach()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "renew the cert", readyAt: "2027-08-15", dueAt: "2027-09-01");

        await h.Issues.PatchIssue("AER-1", Patch(readyAt: "2027-08-20", dueAt: "2027-09-05"), default);

        var kinds = (await h.EventsAsync("AER-1")).Select(e => e.Kind).ToList();
        Assert.Contains(EfHatchIssueEvent.ReadyChanged, kinds);
        Assert.Contains(EfHatchIssueEvent.DueChanged, kinds);
    }

    /// <summary>
    /// The empty string clears a date, the same way it clears a parent - and
    /// the trail says so, because "this stopped being due" is exactly the line
    /// somebody comes looking for.
    /// </summary>
    [Fact]
    public async Task AnEmptyDate_ClearsItAndSaysSo()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "renew the cert", dueAt: "2027-09-01");

        var patched = Value(await h.Issues.PatchIssue("AER-1", Patch(dueAt: ""), default));

        Assert.Null(patched.DueAt);
        var payload = (await h.EventsAsync("AER-1"))[0].Payload!.Value;
        Assert.Equal("2027-09-01", payload.GetProperty("from").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("to").ValueKind);
    }

    /// <summary>
    /// Null is no opinion. A PATCH sent to rename an issue must not quietly
    /// take its dates off it.
    /// </summary>
    [Fact]
    public async Task ADateNotMentioned_IsLeftAlone()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "renew the cert", readyAt: "2027-08-15", dueAt: "2027-09-01");

        var patched = Value(await h.Issues.PatchIssue("AER-1", Patch(title: "renew the wildcard cert"), default));

        Assert.Equal("2027-08-15", patched.ReadyAt);
        Assert.Equal("2027-09-01", patched.DueAt);
    }

    /// <summary>
    /// The round trip a client actually makes: read an issue, change one field,
    /// send the rest back untouched. Handing a date back exactly as it arrived
    /// is not an edit and must not read as one.
    /// </summary>
    [Fact]
    public async Task ADateResentUnchanged_WritesNothing()
    {
        var h = await NewAsync();
        var created = await h.CreateAsync("task", "renew the cert", readyAt: "2027-08-15", dueAt: "2027-09-01T17:00:00Z");

        await h.Issues.PatchIssue("AER-1", Patch(readyAt: created.ReadyAt, dueAt: created.DueAt), default);

        Assert.Single(await h.EventsAsync("AER-1"));
    }

    /// <summary>
    /// The same instant written with an offset is the same instant. It
    /// normalises to UTC on the way in, so it is not an edit either.
    /// </summary>
    [Fact]
    public async Task TheSameInstantInAnotherZone_IsNotAnEdit()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "renew the cert", dueAt: "2027-09-01T21:00:00Z");

        await h.Issues.PatchIssue("AER-1", Patch(dueAt: "2027-09-01T17:00:00-04:00"), default);

        Assert.Single(await h.EventsAsync("AER-1"));
    }

    /// <summary>
    /// A date and an instant at that date's midnight are different promises -
    /// "by the 12th" and "by the 12th at 00:00" - so swapping one for the other
    /// is a change the trail records.
    /// </summary>
    [Fact]
    public async Task AddingATimeOfDayToADate_IsAnEdit()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "renew the cert", dueAt: "2027-09-01");

        var patched = Value(await h.Issues.PatchIssue("AER-1", Patch(dueAt: "2027-09-01T00:00:00Z"), default));

        Assert.Equal("2027-09-01T00:00:00Z", patched.DueAt);
        Assert.Equal(2, (await h.EventsAsync("AER-1")).Count);
    }

    [Fact]
    public async Task ADateThatIsNotADate_IsRefusedWithoutTouchingTheIssue()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "renew the cert", dueAt: "2027-09-01");

        var result = await h.Issues.PatchIssue("AER-1", Patch(title: "renamed", dueAt: "whenever"), default);

        Assert.Contains("dueAt", Reason(result.Result));
        Assert.Equal("renew the cert", Value(await h.Issues.GetIssue("AER-1", default)).Title);
    }

    // ---- The pull request ----

    [Fact]
    public async Task AFreshIssue_PointsAtNoPullRequest()
    {
        var h = await NewAsync();

        var created = await h.CreateAsync("task", "the work");

        Assert.Null(created.PullRequestUrl);
    }

    [Fact]
    public async Task APullRequestUrl_IsSetAndCarriedBack()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "the work");

        var patched = Value(await h.Issues.PatchIssue(
            "AER-1", Patch(pullRequestUrl: "https://example.com/owner/repo/pull/12"), default));

        Assert.Equal("https://example.com/owner/repo/pull/12", patched.PullRequestUrl);
        Assert.Equal("https://example.com/owner/repo/pull/12", Value(await h.Issues.GetIssue("AER-1", default)).PullRequestUrl);
    }

    /// <summary>
    /// The field holds one URL; the trail holds every one it has held. That is
    /// the whole reason this is a scalar and not a list.
    /// </summary>
    [Fact]
    public async Task SettingAPullRequest_WritesOneEventNamingBothEnds()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "the work");

        await h.Issues.PatchIssue("AER-1", Patch(pullRequestUrl: "https://example.com/owner/repo/pull/12"), default);
        await h.Issues.PatchIssue("AER-1", Patch(pullRequestUrl: "https://example.com/owner/repo/pull/13"), default);

        var events = await h.EventsAsync("AER-1");
        Assert.Equal(
            [EfHatchIssueEvent.PullRequestChanged, EfHatchIssueEvent.PullRequestChanged, EfHatchIssueEvent.Created],
            events.Select(e => e.Kind));

        var payload = events[0].Payload!.Value;
        Assert.Equal("https://example.com/owner/repo/pull/12", payload.GetProperty("from").GetString());
        Assert.Equal("https://example.com/owner/repo/pull/13", payload.GetProperty("to").GetString());
    }

    /// <summary>The empty string clears it, the same way it clears a date or a parent.</summary>
    [Fact]
    public async Task AnEmptyPullRequestUrl_ClearsItAndSaysSo()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "the work");
        await h.Issues.PatchIssue("AER-1", Patch(pullRequestUrl: "https://example.com/owner/repo/pull/12"), default);

        var patched = Value(await h.Issues.PatchIssue("AER-1", Patch(pullRequestUrl: ""), default));

        Assert.Null(patched.PullRequestUrl);
        var payload = (await h.EventsAsync("AER-1"))[0].Payload!.Value;
        Assert.Equal("https://example.com/owner/repo/pull/12", payload.GetProperty("from").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("to").ValueKind);
    }

    /// <summary>Null is no opinion. A PATCH sent to rename an issue must not unhook it from its review.</summary>
    [Fact]
    public async Task APullRequestNotMentioned_IsLeftAlone()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "the work");
        await h.Issues.PatchIssue("AER-1", Patch(pullRequestUrl: "https://example.com/owner/repo/pull/12"), default);

        var patched = Value(await h.Issues.PatchIssue("AER-1", Patch(title: "the same work, renamed"), default));

        Assert.Equal("https://example.com/owner/repo/pull/12", patched.PullRequestUrl);
    }

    /// <summary>
    /// The call `hatch.sh pr` makes twice in one session, because the second
    /// push reran it. Re-sending the URL an issue already holds is not an edit
    /// and must not read as one.
    /// </summary>
    [Fact]
    public async Task APullRequestResentUnchanged_WritesNothing()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "the work");
        await h.Issues.PatchIssue("AER-1", Patch(pullRequestUrl: "https://example.com/owner/repo/pull/12"), default);

        await h.Issues.PatchIssue("AER-1", Patch(pullRequestUrl: "https://example.com/owner/repo/pull/12"), default);

        Assert.Equal(2, (await h.EventsAsync("AER-1")).Count);
    }

    /// <summary>Clearing what is already clear is not an edit either.</summary>
    [Fact]
    public async Task ClearingAPullRequestThatIsNotSet_WritesNothing()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "the work");

        await h.Issues.PatchIssue("AER-1", Patch(pullRequestUrl: ""), default);

        Assert.Single(await h.EventsAsync("AER-1"));
    }

    /// <summary>
    /// The rule is that the link opens, not whose forge it points at. A
    /// self-hosted one on a private address is a pull request like any other.
    /// </summary>
    [Theory]
    [InlineData("owner/repo/pull/12")]
    [InlineData("example.com/owner/repo/pull/12")]
    [InlineData("/issues/AER-1")]
    [InlineData("ftp://example.com/owner/repo/pull/12")]
    [InlineData("javascript:alert(1)")]
    public async Task APullRequestUrlThatWouldNotOpen_IsRefusedWithAReason(string text)
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "the work");

        var result = await h.Issues.PatchIssue("AER-1", Patch(pullRequestUrl: text), default);

        Assert.Contains("pull request url", Reason(result.Result));
        Assert.Contains("http", Reason(result.Result));
        Assert.Null(Value(await h.Issues.GetIssue("AER-1", default)).PullRequestUrl);
    }

    [Fact]
    public async Task APullRequestUrlLongerThanTheColumn_IsRefusedWithAReason()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "the work");

        var tooLong = "https://example.com/owner/repo/pull/" + new string('9', EfHatchIssue.MaxPullRequestUrlLength);
        var result = await h.Issues.PatchIssue("AER-1", Patch(pullRequestUrl: tooLong), default);

        Assert.Contains($"{EfHatchIssue.MaxPullRequestUrlLength} characters", Reason(result.Result));
    }

    /// <summary>
    /// Refused before anything is written, like every other bad field on this
    /// endpoint - an edit nobody meant takes nothing with it.
    /// </summary>
    [Fact]
    public async Task ABadPullRequestUrl_IsRefusedWithoutTouchingTheIssue()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "the work");

        var result = await h.Issues.PatchIssue("AER-1", Patch(title: "renamed", pullRequestUrl: "not a url"), default);

        Assert.Contains("pull request url", Reason(result.Result));
        Assert.Equal("the work", Value(await h.Issues.GetIssue("AER-1", default)).Title);
    }

    // ---- Moving ----

    [Fact]
    public async Task AMoveAcrossColumns_WritesAStatusChange()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        await h.Issues.MoveIssue("AER-1", new IssueMoveRequest(h.Done, null, null), default);

        var events = await h.EventsAsync("AER-1");
        Assert.Equal([EfHatchIssueEvent.StatusChanged, EfHatchIssueEvent.Created], events.Select(e => e.Kind));
        Assert.Equal(h.Done, Value(await h.Issues.GetIssue("AER-1", default)).StatusId);
    }

    /// <summary>
    /// Tidying a column is board hygiene, not work. Logging it would bury the
    /// status changes that matter under a hundred lines of dragging.
    /// </summary>
    [Fact]
    public async Task AMoveWithinAColumn_WritesNothing()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "top");
        await h.CreateAsync("story", "bottom");

        await h.Issues.MoveIssue("AER-2", new IssueMoveRequest(h.Inbox, null, "AER-1"), default);

        Assert.Single(await h.EventsAsync("AER-2"));
        var moved = Value(await h.Issues.GetIssue("AER-2", default));
        var stayed = Value(await h.Issues.GetIssue("AER-1", default));
        Assert.True(moved.Rank < stayed.Rank);
    }

    [Fact]
    public async Task AMoveToAColumnThatDoesNotExist_IsRefused()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        var result = await h.Issues.MoveIssue("AER-1", new IssueMoveRequest(9999, null, null), default);

        Assert.Contains("no column", Reason(result.Result));
    }

    // ---- Deleting ----

    /// <summary>
    /// Deleting an epic outdents its stories rather than taking them off the
    /// board with it.
    /// </summary>
    [Fact]
    public async Task DeletingAParent_OutdentsItsChildren()
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "the plan");
        await h.CreateAsync("story", "phase 0", parentKey: "AER-1");

        await h.Issues.DeleteIssue("AER-1", default);

        var orphan = Value(await h.Issues.GetIssue("AER-2", default));
        Assert.Null(orphan.ParentKey);
    }

    [Fact]
    public async Task DeletingAnIssue_TakesItsCommentsAndEventsWithIt()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");
        await h.Thread.AddComment("AER-1", new CommentCreateRequest("a word"), default);

        await h.Issues.DeleteIssue("AER-1", default);

        Assert.Empty(await h.Db.Comments.ToListAsync());
        Assert.Empty(await h.Db.IssueEvents.ToListAsync());
    }

    // ---- Comments ----

    [Fact]
    public async Task ACommentIsRecordedAndLeavesAnEvent()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        await h.Thread.AddComment("AER-1", new CommentCreateRequest("  looked at this  "), default);

        var comments = Value(await h.Thread.GetComments("AER-1", default));
        Assert.Equal("looked at this", Assert.Single(comments).Body);
        Assert.Equal("Nathan", comments[0].Author);
        Assert.Contains(EfHatchIssueEvent.Commented, (await h.EventsAsync("AER-1")).Select(e => e.Kind));
    }

    [Fact]
    public async Task AnEmptyComment_IsRefused()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        var result = await h.Thread.AddComment("AER-1", new CommentCreateRequest("   "), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CommentsAndEventsOnAnUnknownIssue_Are404()
    {
        var h = await NewAsync();

        Assert.IsType<NotFoundResult>((await h.Thread.GetComments("AER-9", default)).Result);
        Assert.IsType<NotFoundResult>((await h.Thread.GetEvents("AER-9", default)).Result);
        Assert.IsType<NotFoundResult>((await h.Thread.AddComment("AER-9", new CommentCreateRequest("hi"), default)).Result);
    }

    // ---- Questions ----

    /* A question is the one comment that something acts on: it stops the issue
       being dispatched (WorkController) and it puts a badge on the card. So
       these pin the two things that would quietly break that - what counts as
       an answer, and what counts as still open. */

    [Fact]
    public async Task AQuestion_IsAskedAndLeavesItsOwnEvent()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        var asked = Created(await h.Thread.AddComment(
            "AER-1", new CommentCreateRequest("per-node or global?", "question"), default));

        Assert.Equal(EfHatchComment.Question, asked.Kind);
        Assert.Null(asked.AnswersId);

        // Its own kind, not "commented": the trail is asked when somebody wants
        // to know when this stopped moving and why.
        Assert.Contains(EfHatchIssueEvent.Asked, (await h.EventsAsync("AER-1")).Select(e => e.Kind));
    }

    [Fact]
    public async Task AnAnswer_ClosesTheQuestionItNames()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");
        var asked = await h.AskAsync("AER-1", "per-node or global?");

        Assert.Single(Value(await h.Questions.GetIssueQuestions("AER-1", open: true, default)));

        await h.AnswerAsync("AER-1", asked.Id, "per-node");

        Assert.Empty(Value(await h.Questions.GetIssueQuestions("AER-1", open: true, default)));

        var all = Value(await h.Questions.GetIssueQuestions("AER-1", open: false, default));
        Assert.Equal("per-node", Assert.Single(Assert.Single(all).Answers).Body);
    }

    [Fact]
    public async Task AnsweringTwice_LeavesBothAnswersAndTheQuestionClosed()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");
        var asked = await h.AskAsync("AER-1", "which disk?");

        await h.AnswerAsync("AER-1", asked.Id, "the bulk one");
        await h.AnswerAsync("AER-1", asked.Id, "on reflection, the reserved one");

        // The link points from the answer to the question, so a second answer
        // is a refinement rather than an edit - and the first stays where it
        // was said.
        Assert.Empty(Value(await h.Questions.GetIssueQuestions("AER-1", open: true, default)));

        var answers = Assert.Single(Value(await h.Questions.GetIssueQuestions("AER-1", open: false, default))).Answers;
        Assert.Equal(["the bulk one", "on reflection, the reserved one"], answers.Select(a => a.Body));
    }

    [Fact]
    public async Task AnAnswerWithNoQuestion_IsRefused()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        var result = await h.Thread.AddComment("AER-1", new CommentCreateRequest("yes", "answer"), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AnAnswerToACommentThatIsNotAQuestion_IsRefused()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");
        var note = Created(await h.Thread.AddComment("AER-1", new CommentCreateRequest("sha abc123"), default));

        var result = await h.Thread.AddComment(
            "AER-1", new CommentCreateRequest("yes", "answer", note.Id), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AnAnswerToAQuestionOnAnotherIssue_IsRefused()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");
        await h.CreateAsync("story", "the other thing");
        var asked = await h.AskAsync("AER-1", "per-node or global?");

        // A cross-issue link would put the answer on a thread nobody reading
        // the question can see, and would leave AER-1 blocked forever.
        var result = await h.Thread.AddComment(
            "AER-2", new CommentCreateRequest("per-node", "answer", asked.Id), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Single(Value(await h.Questions.GetIssueQuestions("AER-1", open: true, default)));
    }

    [Fact]
    public async Task ANoteThatNamesAQuestion_IsRefused()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");
        var asked = await h.AskAsync("AER-1", "per-node or global?");

        // Sent by a client that has misunderstood something. Dropping the half
        // it got wrong is how it stays misunderstood.
        var result = await h.Thread.AddComment(
            "AER-1", new CommentCreateRequest("thinking about it", null, asked.Id), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AKindNobodyDefined_IsRefused()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        var result = await h.Thread.AddComment("AER-1", new CommentCreateRequest("hm", "musing"), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task ACommentWithNoKind_IsStillJustAComment()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        await h.Thread.AddComment("AER-1", new CommentCreateRequest("sha abc123"), default);

        // The whole thread predating questions has to keep reading as notes,
        // and so does every client that has never heard of a kind.
        var comment = Assert.Single(Value(await h.Thread.GetComments("AER-1", default)));
        Assert.Equal(EfHatchComment.Note, comment.Kind);
        Assert.Empty(Value(await h.Questions.GetQuestions(open: true, default)));
    }

    [Fact]
    public async Task TheHouseWideList_IsEveryOpenQuestionOldestFirst()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");
        await h.CreateAsync("story", "the other thing");

        var first = await h.AskAsync("AER-1", "asked first");
        await h.AskAsync("AER-2", "asked second");
        await h.AskAsync("AER-1", "asked third");
        await h.AnswerAsync("AER-1", first.Id, "settled");

        var open = Value(await h.Questions.GetQuestions(open: true, default));

        // Oldest first is answering order: the question that has waited longest
        // is holding something up longest.
        Assert.Equal(["asked second", "asked third"], open.Select(q => q.Body));
        Assert.Equal(["AER-2", "AER-1"], open.Select(q => q.IssueKey));
    }

    [Fact]
    public async Task TheBoard_CountsWhatEachCardIsWaitingOn()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");
        await h.CreateAsync("story", "the quiet one");

        var answered = await h.AskAsync("AER-1", "already settled");
        await h.AnswerAsync("AER-1", answered.Id, "yes");
        await h.AskAsync("AER-1", "still open");
        await h.AskAsync("AER-1", "also open");

        var board = Value(await h.Board.GetBoard(default));

        Assert.Equal(2, board.Issues.Single(i => i.Key == "AER-1").OpenQuestions);
        Assert.Equal(0, board.Issues.Single(i => i.Key == "AER-2").OpenQuestions);
    }

    // ---- Options on a question ----

    /* Options are what turn a question from a paragraph into something the
       operator presses. Every rule pinned here is about the menu arriving
       readable - a question nobody can act on strands the ticket that carries
       it, and the dispatch stays refused until somebody does. */

    [Fact]
    public async Task AQuestionCarriesTheAnswersItOffers()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        var asked = Created(await h.Thread.AddComment("AER-1", new CommentCreateRequest(
            "How should the epic meter weight its children?",
            EfHatchComment.Question,
            Options:
            [
                new QuestionOptionDto("Child-weighted", "Each story is 1/n of its epic whatever its size", Recommended: true),
                new QuestionOptionDto("Leaf-weighted", "Every task counts equally, so the bar tracks work remaining"),
            ]), default));

        Assert.NotNull(asked.Options);
        Assert.Equal(["Child-weighted", "Leaf-weighted"], asked.Options.Select(o => o.Label));
        Assert.True(asked.Options[0].Recommended);
        Assert.False(asked.Options[1].Recommended);

        // And they survive the round trip through jsonb, which is the half that
        // a hand-rolled serializer would get wrong.
        var question = Assert.Single(Value(await h.Questions.GetIssueQuestions("AER-1", open: true, default)));
        Assert.Equal("Each story is 1/n of its epic whatever its size", question.Options![0].Detail);
    }

    [Fact]
    public async Task AQuestionAskedInProse_OffersNothingRatherThanAnEmptyMenu()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        await h.AskAsync("AER-1", "What should this be called?");

        // Null, not []. "Asked in prose" and "offered a menu with nothing on
        // it" are different things, and only the first one ever happens.
        var question = Assert.Single(Value(await h.Questions.GetIssueQuestions("AER-1", open: true, default)));
        Assert.Null(question.Options);
    }

    [Fact]
    public async Task AnswersAndNotes_MayNotOfferOptions()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");
        var asked = await h.AskAsync("AER-1", "which way?");

        var option = new QuestionOptionDto[] { new("this way"), new("that way") };

        Assert.IsType<BadRequestObjectResult>(
            (await h.Thread.AddComment("AER-1", new CommentCreateRequest("a note", Options: option), default)).Result);

        Assert.IsType<BadRequestObjectResult>(
            (await h.Thread.AddComment("AER-1", new CommentCreateRequest(
                "this way", EfHatchComment.Answer, asked.Id, option), default)).Result);
    }

    [Theory]
    [InlineData(1, "not a question")]
    [InlineData(QuestionOptionDto.MaxPerQuestion + 1, "at most")]
    public async Task AMenuOfTheWrongSize_IsRefused(int count, string because)
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        var options = Enumerable.Range(1, count).Select(n => new QuestionOptionDto($"option {n}")).ToList();

        var result = await h.Thread.AddComment(
            "AER-1", new CommentCreateRequest("which?", EfHatchComment.Question, Options: options), default);

        Assert.Contains(because, Assert.IsType<BadRequestObjectResult>(result.Result).Value?.ToString());
    }

    [Fact]
    public async Task TwoOptionsWithTheSameLabel_AreRefused()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        // Case-insensitively the same, because the label becomes the answer's
        // own body - two that differ only by case leave a thread nobody can
        // read back to find out which was taken.
        var result = await h.Thread.AddComment("AER-1", new CommentCreateRequest(
            "which?", EfHatchComment.Question,
            Options: [new QuestionOptionDto("Per-node"), new QuestionOptionDto("per-node")]), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task TwoRecommendations_AreRefused()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        var result = await h.Thread.AddComment("AER-1", new CommentCreateRequest(
            "which?", EfHatchComment.Question,
            Options:
            [
                new QuestionOptionDto("this way", Recommended: true),
                new QuestionOptionDto("that way", Recommended: true),
            ]), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AnOptionWithNoLabel_IsRefused()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        var result = await h.Thread.AddComment("AER-1", new CommentCreateRequest(
            "which?", EfHatchComment.Question,
            Options: [new QuestionOptionDto("   "), new QuestionOptionDto("that way")]), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AnOptionLabelLongEnoughToBeAParagraph_IsRefused()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        // A label is pressed and read back; the reasoning belongs in the detail
        // beside it, which is allowed to run long.
        var result = await h.Thread.AddComment("AER-1", new CommentCreateRequest(
            "which?", EfHatchComment.Question,
            Options:
            [
                new QuestionOptionDto(new string('x', QuestionOptionDto.MaxLabelLength + 1)),
                new QuestionOptionDto("that way"),
            ]), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task OptionsAreStoredTrimmed()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");

        var asked = Created(await h.Thread.AddComment("AER-1", new CommentCreateRequest(
            "which?", EfHatchComment.Question,
            Options:
            [
                new QuestionOptionDto("  Per-node  ", "  one budget a node  "),
                new QuestionOptionDto("Global", "   "),
            ]), default));

        Assert.Equal("Per-node", asked.Options![0].Label);
        Assert.Equal("one budget a node", asked.Options[0].Detail);

        // Whitespace is not a detail. Blank comes back absent, so the UI has one
        // thing to test rather than two.
        Assert.Null(asked.Options[1].Detail);
    }

    [Fact]
    public async Task AnOptionsColumnThatWillNotParse_ReadsAsAskedInProse()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");
        var asked = await h.AskAsync("AER-1", "which?");

        // Hand-edited, or written by a shape this build does not know. The
        // question is still a question and is still answerable in the box; only
        // the menu is gone. Throwing here would strand the ticket instead.
        var row = await h.Db.Comments.SingleAsync(c => c.Id == asked.Id);
        row.Options = "{ this is not a list";
        await h.Db.SaveChangesAsync();

        var question = Assert.Single(Value(await h.Questions.GetIssueQuestions("AER-1", open: true, default)));
        Assert.Null(question.Options);
        Assert.Equal("which?", question.Body);
    }

    [Fact]
    public async Task DeletingAnIssue_TakesItsQuestionsAndAnswersWithIt()
    {
        var h = await NewAsync();
        await h.CreateAsync("story", "the thing");
        var asked = await h.AskAsync("AER-1", "per-node or global?");
        await h.AnswerAsync("AER-1", asked.Id, "per-node");

        // The answer points at the question, so this is the delete that a
        // RESTRICT on that link would have refused - see the CommentQuestions
        // migration.
        await h.Issues.DeleteIssue("AER-1", default);

        Assert.Empty(await h.Db.Comments.ToListAsync());
    }

    [Fact]
    public async Task QuestionsOnAnUnknownIssue_Are404()
    {
        var h = await NewAsync();

        Assert.IsType<NotFoundResult>((await h.Questions.GetIssueQuestions("AER-9", open: true, default)).Result);
    }

    // ---- Validation ----

    [Theory]
    [InlineData("", "story", "needs a title")]
    [InlineData("   ", "story", "needs a title")]
    [InlineData("fine", "chore", "one of epic")]
    [InlineData("fine", "", "one of epic")]
    public async Task AMalformedIssue_IsRefusedWithAReason(string title, string type, string expected)
    {
        var h = await NewAsync();

        var result = await h.Issues.CreateIssue(new IssueCreateRequest(h.ProjectId, type, title, null, null, null, null), default);

        Assert.Contains(expected, Reason(result.Result));
    }

    [Fact]
    public async Task AnIssueInAProjectThatDoesNotExist_IsA404()
    {
        var h = await NewAsync();

        var result = await h.Issues.CreateIssue(new IssueCreateRequest(9999, "task", "orphan", null, null, null, null), default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- The board ----

    [Fact]
    public async Task TheBoard_ReturnsEveryColumnInOrderAndEveryCardInTheHouse()
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "the plan");
        await h.CreateAsync("story", "phase 0", parentKey: "AER-1");
        await h.CreateAsync("task", "ops work", projectId: h.OtherProjectId);
        await h.Issues.MoveIssue("AER-2", new IssueMoveRequest(h.Done, null, null), default);

        var board = Value(await h.Board.GetBoard(default));

        Assert.Equal(["inbox", "todo", "done"], board.Statuses.Select(s => s.Name));
        Assert.Equal(["AER-1", "OPS-1", "AER-2"], board.Issues.Select(i => i.Key));
        Assert.Equal("AER-1", board.Issues.Single(i => i.Key == "AER-2").ParentKey);
        Assert.Equal("OPS", board.Issues.Single(i => i.Key == "OPS-1").ProjectKey);
    }

    /// <summary>
    /// Including the ones nobody can work on yet. The browser folds those away
    /// and the server does not - a client that does not know about the fold has
    /// to be able to tell an empty board from a filtered one.
    /// </summary>
    [Fact]
    public async Task TheBoard_CarriesBothDatesAndHidesNothing()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "not yet", readyAt: "2027-08-15", dueAt: "2027-09-01");
        await h.CreateAsync("task", "right now");

        var board = Value(await h.Board.GetBoard(default));

        Assert.Equal(["AER-1", "AER-2"], board.Issues.Select(i => i.Key));
        var waiting = board.Issues.Single(i => i.Key == "AER-1");
        Assert.Equal("2027-08-15", waiting.ReadyAt);
        Assert.Equal("2027-09-01", waiting.DueAt);
        Assert.Null(board.Issues.Single(i => i.Key == "AER-2").ReadyAt);
    }

    /// <summary>
    /// The assignee, on the two reads a client actually draws from - and the
    /// one thing that has to be true of both: an id that no longer resolves is
    /// nobody, in the same breath and without a 500.
    /// </summary>
    [Fact]
    public async Task AnAssignee_RidesTheIssueAndTheCard()
    {
        var h = await NewAsync();
        var ada = h.Actors.AddPerson("Ada");
        await h.CreateAsync("task", "hers");
        await h.AssignAsync("AER-1", ada.Id);

        var issue = Value(await h.Issues.GetIssue("AER-1", default));
        Assert.Equal("person", issue.Assignee!.Kind);
        Assert.Equal(ada.Id, issue.Assignee.Id);
        Assert.Equal("Ada", issue.Assignee.Name);

        var card = Value(await h.Board.GetBoard(default)).Issues.Single(i => i.Key == "AER-1");
        Assert.Equal(ada.Id, card.Assignee!.Id);
    }

    [Fact]
    public async Task AnIssueNobodyOwns_SaysSoAsNull()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "nobody's");

        // Null is the only way "nobody" is said - there is no empty-object form
        // for a client to have to know about.
        Assert.Null(Value(await h.Issues.GetIssue("AER-1", default)).Assignee);
        Assert.Null(Value(await h.Board.GetBoard(default)).Issues.Single().Assignee);
    }

    [Fact]
    public async Task AnAssigneeWhoIsGone_ReadsAsNobodyRatherThanThrowing()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "was somebody's");

        // The column holds an id the directory does not know: a deleted person,
        // or a key that has since been revoked. Nothing swept it and nothing
        // needs to.
        await h.AssignAsync("AER-1", Guid.NewGuid());

        Assert.Null(Value(await h.Issues.GetIssue("AER-1", default)).Assignee);
        Assert.Null(Value(await h.Board.GetBoard(default)).Issues.Single().Assignee);
    }

    // ---- Delete guards ----

    [Fact]
    public async Task AProjectWithIssuesInIt_CannotBeDeleted()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "the thing");

        var result = await h.Projects.DeleteProject(h.ProjectId, default);

        Assert.Contains("still has 1 issue", Reason(result));
    }

    [Fact]
    public async Task AnEmptyProject_CanBeDeleted()
    {
        var h = await NewAsync();

        Assert.IsType<NoContentResult>(await h.Projects.DeleteProject(h.OtherProjectId, default));
    }

    [Fact]
    public async Task AColumnWithCardsInIt_CannotBeDeleted()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "the thing");

        var result = await h.Statuses.DeleteStatus(h.Inbox, default);

        Assert.Contains("still holds 1 issue", Reason(result));
    }

    [Fact]
    public async Task AnEmptyColumn_CanBeDeleted()
    {
        var h = await NewAsync();

        Assert.IsType<NoContentResult>(await h.Statuses.DeleteStatus(h.Done, default));
    }

    /// <summary>
    /// A board with no columns has nowhere to put a new issue, and no page that
    /// can put a column back. The last one stays.
    /// </summary>
    [Fact]
    public async Task TheLastColumn_CannotBeDeleted()
    {
        var h = await NewAsync();
        await h.Statuses.DeleteStatus(h.Done, default);
        await h.Statuses.DeleteStatus(h.Todo, default);

        var result = await h.Statuses.DeleteStatus(h.Inbox, default);

        Assert.Contains("at least one column", Reason(result));
    }

    // ---- Projects and statuses ----

    [Theory]
    [InlineData("A")]
    [InlineData("TOOLONGKEY")]
    [InlineData("1AB")]
    public async Task AMalformedProjectKey_IsRefused(string key)
    {
        var h = await NewAsync();

        var result = await h.Projects.CreateProject(new ProjectCreateRequest(key, "Something"), default);

        Assert.Contains("two to six letters or digits", Reason(result.Result));
    }

    /// <summary>
    /// A key typed in lower case is a key typed in lower case, not a mistake.
    /// It is upper-cased on the way in - the same normalization
    /// <see cref="IssueKey.TryParse"/> does on the way back out.
    /// </summary>
    [Fact]
    public async Task AKeyTypedInLowerCase_IsShoutedRatherThanRefused()
    {
        var h = await NewAsync();

        var created = Created(await h.Projects.CreateProject(new ProjectCreateRequest("hat", "Hatch"), default));

        Assert.Equal("HAT", created.Key);
    }

    [Fact]
    public async Task ADuplicateProjectKey_Is409()
    {
        var h = await NewAsync();

        var result = await h.Projects.CreateProject(new ProjectCreateRequest("aer", "Again"), default);

        Assert.Contains("already taken", Reason(result.Result));
    }

    [Fact]
    public async Task RenamingAProject_LeavesItsKeyAlone()
    {
        var h = await NewAsync();

        var patched = Value(await h.Projects.PatchProject(h.ProjectId, new ProjectPatchRequest("The House"), default));

        Assert.Equal("The House", patched.Name);
        Assert.Equal("AER", patched.Key);
    }

    // ---- Rekeying ----

    /// <summary>
    /// The point of the whole feature, and the thing that would make it not
    /// worth having if it were false: a rekeyed project keeps its tree. Every
    /// story stays under its epic and every task under its story, because
    /// parentage is a foreign key and the key is only a prefix.
    /// </summary>
    [Fact]
    public async Task RekeyingAProject_KeepsEveryParentAndChildHookedUp()
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "the plan");
        await h.CreateAsync("story", "phase 0", parentKey: "AER-1");
        await h.CreateAsync("task", "first checkbox", parentKey: "AER-2");

        await h.Projects.PatchProject(h.ProjectId, new ProjectPatchRequest(null, "HAT"), default);

        Assert.Equal("HAT-1", Value(await h.Issues.GetIssue("HAT-2", default)).ParentKey);
        Assert.Equal("HAT-2", Value(await h.Issues.GetIssue("HAT-3", default)).ParentKey);
        Assert.Equal(["HAT-2"], Value(await h.Issues.GetIssue("HAT-1", default)).ChildKeys);
    }

    /// <summary>
    /// The cost, stated as a test so nobody is surprised by it: the old key
    /// names nothing afterwards. Text that spelled it out - a commit message, a
    /// chat log - is pointing at a dead link, and that is the trade the operator
    /// is shown before the speed bump lets them through.
    /// </summary>
    [Fact]
    public async Task AfterARekey_TheOldKeyIsA404()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "the thing");

        await h.Projects.PatchProject(h.ProjectId, new ProjectPatchRequest(null, "HAT"), default);

        Assert.IsType<NotFoundResult>((await h.Issues.GetIssue("AER-1", default)).Result);
        Assert.Equal("the thing", Value(await h.Issues.GetIssue("HAT-1", default)).Title);
    }

    /// <summary>
    /// Numbers are the project's, not the key's. A rekey must not hand HAT-1 to
    /// a second issue while AER-1 is still sitting there wearing it.
    /// </summary>
    [Fact]
    public async Task ARekey_DoesNotResetTheNumbering()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "first");
        await h.CreateAsync("task", "second");

        await h.Projects.PatchProject(h.ProjectId, new ProjectPatchRequest(null, "HAT"), default);
        var next = await h.CreateAsync("task", "third");

        Assert.Equal("HAT-3", next.Key);
    }

    [Fact]
    public async Task ARekeyToATakenKey_Is409()
    {
        var h = await NewAsync();

        var result = await h.Projects.PatchProject(h.ProjectId, new ProjectPatchRequest(null, "OPS"), default);

        Assert.Contains("already taken", Reason(result.Result));
        Assert.Equal("AER", Value(await h.Projects.GetProjects(default)).First(p => p.Id == h.ProjectId).Key);
    }

    /// <summary>A project is not in conflict with itself - re-sending its own key is a no-op, not a 409.</summary>
    [Fact]
    public async Task ARekeyToTheKeyItAlreadyHas_IsFine()
    {
        var h = await NewAsync();

        var patched = Value(await h.Projects.PatchProject(h.ProjectId, new ProjectPatchRequest(null, "aer"), default));

        Assert.Equal("AER", patched.Key);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("TOOLONGKEY")]
    [InlineData("1AB")]
    [InlineData("A-B")]
    public async Task AMalformedNewKey_IsRefused(string key)
    {
        var h = await NewAsync();

        var result = await h.Projects.PatchProject(h.ProjectId, new ProjectPatchRequest(null, key), default);

        Assert.Contains("two to six letters or digits", Reason(result.Result));
    }

    [Fact]
    public async Task ARekey_ShoutsALowerCaseKeyRatherThanRefusingIt()
    {
        var h = await NewAsync();

        var patched = Value(await h.Projects.PatchProject(h.ProjectId, new ProjectPatchRequest(null, "hat"), default));

        Assert.Equal("HAT", patched.Key);
    }

    /// <summary>
    /// Null is no opinion here too. A request that only rekeys must not have to
    /// re-send a name, and must not blank the one already there.
    /// </summary>
    [Fact]
    public async Task ARekeyThatMentionsNoName_LeavesTheNameAlone()
    {
        var h = await NewAsync();

        var patched = Value(await h.Projects.PatchProject(h.ProjectId, new ProjectPatchRequest(null, "HAT"), default));

        Assert.Equal("Aerie", patched.Name);
    }

    [Fact]
    public async Task ARenameToNothing_IsRefused()
    {
        var h = await NewAsync();

        var result = await h.Projects.PatchProject(h.ProjectId, new ProjectPatchRequest("   "), default);

        Assert.Contains("needs a name", Reason(result.Result));
    }

    [Fact]
    public async Task ANewColumn_GoesOnTheRight()
    {
        var h = await NewAsync();

        var created = Created(await h.Statuses.CreateStatus(new StatusCreateRequest("review", null, null), default));

        Assert.Equal(["inbox", "todo", "done", "review"], Value(await h.Statuses.GetStatuses(default)).Select(s => s.Name));
        Assert.False(created.IsTerminal);
    }

    [Fact]
    public async Task ReorderingAColumn_MovesIt()
    {
        var h = await NewAsync();

        await h.Statuses.PatchStatus(h.Done, new StatusPatchRequest(null, 5, null), default);

        Assert.Equal(["done", "inbox", "todo"], Value(await h.Statuses.GetStatuses(default)).Select(s => s.Name));
    }

    [Fact]
    public async Task ADuplicateColumnName_Is409()
    {
        var h = await NewAsync();

        var result = await h.Statuses.CreateStatus(new StatusCreateRequest("todo", null, null), default);

        Assert.Contains("already a \"todo\" column", Reason(result.Result));
    }

    // ---- Column colours ----

    [Fact]
    public async Task AColumnWithNothingSaidAboutItsColour_WearsTheDefault()
    {
        var h = await NewAsync();

        var created = Created(await h.Statuses.CreateStatus(new StatusCreateRequest("review", null, null), default));

        Assert.Equal(EfHatchStatus.DefaultColor, created.Color);
    }

    /// <summary>
    /// One stored shape, lower case, so two spellings of one colour compare
    /// equal - see <see cref="EfHatchStatus.NormalizeColor"/>.
    /// </summary>
    [Fact]
    public async Task AColourIsStoredInOneCase()
    {
        var h = await NewAsync();

        var created = Created(await h.Statuses.CreateStatus(new StatusCreateRequest("review", null, null, "#AB12EF"), default));

        Assert.Equal("#ab12ef", created.Color);
    }

    [Fact]
    public async Task RecolouringAColumn_KeepsEverythingElseAboutIt()
    {
        var h = await NewAsync();

        var patched = Value(await h.Statuses.PatchStatus(h.Todo, new StatusPatchRequest(null, null, null, "#2a78d6"), default));

        Assert.Equal("#2a78d6", patched.Color);
        Assert.Equal("todo", patched.Name);
        Assert.Equal(20, patched.SortOrder);
    }

    [Fact]
    public async Task RenamingAColumn_DoesNotTakeItsColourOff()
    {
        var h = await NewAsync();
        await h.Statuses.PatchStatus(h.Todo, new StatusPatchRequest(null, null, null, "#2a78d6"), default);

        var patched = Value(await h.Statuses.PatchStatus(h.Todo, new StatusPatchRequest("next up", null, null), default));

        Assert.Equal("#2a78d6", patched.Color);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#ab1")]
    [InlineData("6b7280")]
    [InlineData("#gggggg")]
    [InlineData("rgb(1,2,3)")]
    public async Task AColourThatIsNotAHexValue_IsRefusedWithAReason(string color)
    {
        var h = await NewAsync();

        var result = await h.Statuses.CreateStatus(new StatusCreateRequest("review", null, null, color), default);

        Assert.Contains("hex value", Reason(result.Result));
    }

    /// <summary>
    /// The board draws the colour, so the board has to carry it - the columns
    /// come from one request and there is no second call to look one up.
    /// </summary>
    [Fact]
    public async Task TheBoard_CarriesEachColumnsColour()
    {
        var h = await NewAsync();
        await h.Statuses.PatchStatus(h.Done, new StatusPatchRequest(null, null, null, "#008300"), default);

        var board = Value(await h.Board.GetBoard(default));

        Assert.Equal("#008300", board.Statuses.Single(s => s.Id == h.Done).Color);
    }

    // ---- Searching ----

    [Fact]
    public async Task ASearchWithNoFilters_IsTheWholeBoard()
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "the plan");
        await h.CreateAsync("task", "a thing");

        Assert.Equal(["AER-1", "AER-2"], await h.SearchAsync());
    }

    [Fact]
    public async Task ASearchByType_ReturnsOnlyThatType()
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "the plan");
        await h.CreateAsync("bug", "the leak");

        Assert.Equal(["AER-2"], await h.SearchAsync(type: "bug"));
    }

    [Fact]
    public async Task ASearchByStatus_ReturnsOnlyThatColumn()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "still filed");
        await h.CreateAsync("task", "started");
        await h.Issues.PatchIssue("AER-2", Patch(statusId: h.Todo), default);

        Assert.Equal(["AER-2"], await h.SearchAsync(statusId: h.Todo));
    }

    [Fact]
    public async Task ASearchByParent_ReturnsItsChildrenAndNotItsGrandchildren()
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "the plan");
        await h.CreateAsync("story", "phase 0", parentKey: "AER-1");
        await h.CreateAsync("task", "a checkbox", parentKey: "AER-2");

        Assert.Equal(["AER-2"], await h.SearchAsync(parentKey: "AER-1"));
    }

    /// <summary>
    /// The empty string is the one filter that cannot be written any other way:
    /// "no parent at all", which is how the orphans get found. It reads the same
    /// as it does on a patch body, where empty clears the parent.
    /// </summary>
    [Fact]
    public async Task ASearchForNoParent_FindsTheOrphans()
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "the plan");
        await h.CreateAsync("story", "phase 0", parentKey: "AER-1");

        Assert.Equal(["AER-1"], await h.SearchAsync(parentKey: ""));
    }

    /// <summary>
    /// The reason the ancestor filter exists: everything under an epic, at every
    /// level, in one request - and not the epic itself, because "under AER-1" is
    /// a question about what hangs beneath it.
    /// </summary>
    [Fact]
    public async Task ASearchByAncestor_ReachesEveryLevelBelowItAndNotTheAncestor()
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "the plan");
        await h.CreateAsync("story", "phase 0", parentKey: "AER-1");
        await h.CreateAsync("task", "a checkbox", parentKey: "AER-2");
        await h.CreateAsync("task", "another checkbox", parentKey: "AER-2");
        await h.CreateAsync("task", "unrelated");

        Assert.Equal(["AER-2", "AER-3", "AER-4"], await h.SearchAsync(ancestorKey: "AER-1"));
    }

    [Fact]
    public async Task ASearchByAncestorOfALeaf_FindsNothingRatherThanFailing()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "on its own");

        Assert.Empty(await h.SearchAsync(ancestorKey: "AER-1"));
    }

    [Fact]
    public async Task ATextSearch_MatchesPartOfATitleInAnyCase()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "Renew the certificate");
        await h.CreateAsync("task", "Feed the cat");

        Assert.Equal(["AER-1"], await h.SearchAsync(text: "CERTIF"));
    }

    /// <summary>
    /// A key pasted into a search box is a lookup. The key is computed rather
    /// than stored, so nothing about a substring match on a title would ever
    /// find it.
    /// </summary>
    [Fact]
    public async Task ATextSearchForAKey_FindsThatIssue()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "nothing alike");
        await h.CreateAsync("task", "nor this");

        Assert.Equal(["AER-2"], await h.SearchAsync(text: "aer-2"));
    }

    [Fact]
    public async Task FiltersCombine()
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "the plan");
        await h.CreateAsync("story", "phase 0", parentKey: "AER-1");
        await h.CreateAsync("bug", "phase 0 leaks", parentKey: "AER-1");

        Assert.Equal(["AER-3"], await h.SearchAsync(ancestorKey: "AER-1", type: "bug"));
    }

    [Fact]
    public async Task ASearchByProject_StaysInIt()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "house work");
        await h.CreateAsync("task", "ops work", projectId: h.OtherProjectId);

        Assert.Equal(["OPS-1"], await h.SearchAsync(projectId: h.OtherProjectId));
    }

    [Fact]
    public async Task ASearchForATypeThatIsNotOne_IsRefusedWithAReason()
    {
        var h = await NewAsync();

        var result = await h.Issues.SearchIssues(null, "epicc", null, null, null, null, default);

        Assert.Contains("epic, story, task, bug", Reason(result.Result));
    }

    [Fact]
    public async Task ASearchUnderAnIssueThatDoesNotExist_SaysSo()
    {
        var h = await NewAsync();

        var result = await h.Issues.SearchIssues(null, null, null, null, "AER-99", null, default);

        Assert.Contains("there is no AER-99", Reason(result.Result));
    }

    // ---- Editing in bulk ----

    [Fact]
    public async Task ABulkEdit_MovesEveryIssueItNames()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "one");
        await h.CreateAsync("task", "two");
        await h.CreateAsync("task", "untouched");

        var result = Value(await h.Issues.BulkEdit(Bulk(["AER-1", "AER-2"], statusId: h.Todo), default));

        Assert.Equal(["AER-1", "AER-2"], result.Changed);
        Assert.Equal(["AER-1", "AER-2"], await h.SearchAsync(statusId: h.Todo));
        Assert.Equal(h.Inbox, Value(await h.Issues.GetIssue("AER-3", default)).StatusId);
    }

    /// <summary>
    /// Nothing is saved until the batch finishes, so every issue in it would be
    /// told the same "bottom of the column" if the ranks were asked for one at a
    /// time. Ties are survivable and are still not what was meant - the order
    /// the list was in is the order the column should end up in.
    /// </summary>
    [Fact]
    public async Task ABulkMoveIntoOneColumn_LandsThemInOrderRatherThanOnTopOfEachOther()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "one");
        await h.CreateAsync("task", "two");
        await h.CreateAsync("task", "three");

        await h.Issues.BulkEdit(Bulk(["AER-1", "AER-2", "AER-3"], statusId: h.Todo), default);

        var ranks = new List<long>();
        foreach (var key in new[] { "AER-1", "AER-2", "AER-3" })
            ranks.Add(Value(await h.Issues.GetIssue(key, default)).Rank);

        Assert.Equal(ranks.Count, ranks.Distinct().Count());
        Assert.Equal(ranks.OrderBy(r => r), ranks);
    }

    [Fact]
    public async Task ABulkEdit_TouchesOnlyTheFieldsItNames()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "renew the cert", dueAt: "2027-09-01");

        await h.Issues.BulkEdit(Bulk(["AER-1"], statusId: h.Todo), default);

        var issue = Value(await h.Issues.GetIssue("AER-1", default));
        Assert.Equal("renew the cert", issue.Title);
        Assert.Equal("2027-09-01", issue.DueAt);
        Assert.Equal("task", issue.Type);
    }

    [Fact]
    public async Task ABulkEdit_WritesOneEventPerChangedFieldPerIssue()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "one");
        await h.CreateAsync("task", "two");

        await h.Issues.BulkEdit(Bulk(["AER-1", "AER-2"], statusId: h.Todo, dueAt: "2027-09-01"), default);

        foreach (var key in new[] { "AER-1", "AER-2" })
        {
            var kinds = (await h.EventsAsync(key)).Select(e => e.Kind).ToList();
            Assert.Equal([EfHatchIssueEvent.DueChanged, EfHatchIssueEvent.StatusChanged, EfHatchIssueEvent.Created], kinds);
        }
    }

    /// <summary>
    /// Running the same bulk edit twice is not two edits. An issue already
    /// holding every named value comes back unchanged and writes nothing, which
    /// is what keeps a trail readable after somebody presses Apply twice.
    /// </summary>
    [Fact]
    public async Task ABulkEditAppliedTwice_IsOneEdit()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "one");
        await h.Issues.BulkEdit(Bulk(["AER-1"], statusId: h.Todo), default);

        var again = Value(await h.Issues.BulkEdit(Bulk(["AER-1"], statusId: h.Todo), default));

        Assert.Empty(again.Changed);
        Assert.Equal(["AER-1"], again.Unchanged);
        Assert.Equal(2, (await h.EventsAsync("AER-1")).Count);
    }

    [Fact]
    public async Task ABulkEditWithAnEmptyDate_ClearsItEverywhere()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "one", dueAt: "2027-09-01");
        await h.CreateAsync("task", "two", dueAt: "2027-10-01");

        var result = Value(await h.Issues.BulkEdit(Bulk(["AER-1", "AER-2"], dueAt: ""), default));

        Assert.Equal(["AER-1", "AER-2"], result.Changed);
        Assert.Null(Value(await h.Issues.GetIssue("AER-1", default)).DueAt);
        Assert.Null(Value(await h.Issues.GetIssue("AER-2", default)).DueAt);
    }

    [Fact]
    public async Task ABulkEditNamingAnIssueThatIsNotThere_ReportsItAndDoesTheRest()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "one");

        var result = Value(await h.Issues.BulkEdit(Bulk(["AER-1", "AER-99"], statusId: h.Todo), default));

        Assert.Equal(["AER-1"], result.Changed);
        Assert.Equal("AER-99", result.Failures.Single().Key);
        Assert.Contains("there is no AER-99", result.Failures.Single().Reason);
    }

    /// <summary>
    /// The property the whole bulk path is built around, and the reason
    /// <c>StageEditAsync</c> looks everything up before it writes anything: one
    /// issue's refusal must leave that issue exactly as it was, while the batch
    /// around it goes through. A mutation staged before the refusal would be
    /// saved by the batch's own SaveChanges.
    /// </summary>
    [Fact]
    public async Task AnIssueThatRefusesTheEdit_IsLeftCompletelyUntouched()
    {
        var h = await NewAsync();
        await h.CreateAsync("epic", "the plan");
        await h.CreateAsync("story", "phase 0");
        await h.CreateAsync("task", "a checkbox");

        // A story hangs under an epic and a task does not, so the third issue
        // refuses the parent while the second takes it.
        var result = Value(await h.Issues.BulkEdit(
            Bulk(["AER-2", "AER-3"], statusId: h.Todo, parentKey: "AER-1"), default));

        Assert.Equal(["AER-2"], result.Changed);
        Assert.Equal("AER-3", result.Failures.Single().Key);

        var refused = Value(await h.Issues.GetIssue("AER-3", default));
        Assert.Equal(h.Inbox, refused.StatusId);
        Assert.Null(refused.ParentKey);
        Assert.Single(await h.EventsAsync("AER-3"));
    }

    [Fact]
    public async Task ABulkEditNamingNoIssues_IsRefused()
    {
        var h = await NewAsync();

        var result = await h.Issues.BulkEdit(Bulk([], statusId: h.Todo), default);

        Assert.Contains("name at least one issue", Reason(result.Result));
    }

    [Fact]
    public async Task ABulkEditThatChangesNothing_IsRefusedRatherThanRunAgainstEveryIssue()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "one");

        var result = await h.Issues.BulkEdit(Bulk(["AER-1"]), default);

        Assert.Contains("has to change something", Reason(result.Result));
    }

    /// <summary>
    /// Something wrong with the edit itself is the whole request's problem, not
    /// a hundred identical failures - and nothing is written before it is found.
    /// </summary>
    [Theory]
    [InlineData("epicc", null, null)]
    [InlineData(null, "whenever", null)]
    [InlineData(null, null, "soon")]
    public async Task ABulkEditThatIsNotAnEdit_RefusesTheWholeRequest(string? type, string? readyAt, string? dueAt)
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "one");

        var result = await h.Issues.BulkEdit(
            new IssueBulkEditRequest(["AER-1"], type, null, null, readyAt, dueAt), default);

        Assert.Contains("400", Reason(result.Result));
        Assert.Single(await h.EventsAsync("AER-1"));
    }

    [Fact]
    public async Task ABulkEditToAColumnThatIsNotThere_RefusesTheWholeRequest()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "one");

        var result = Value(await h.Issues.BulkEdit(Bulk(["AER-1"], statusId: 9999), default));

        Assert.Contains("there is no column 9999", result.Failures.Single().Reason);
        Assert.Empty(result.Changed);
    }

    /// <summary>A client that named one issue twice meant it once - that is not a failure to report.</summary>
    [Fact]
    public async Task ABulkEditNamingTheSameIssueTwice_CountsItOnce()
    {
        var h = await NewAsync();
        await h.CreateAsync("task", "one");

        var result = Value(await h.Issues.BulkEdit(Bulk(["AER-1", "aer-1"], statusId: h.Todo), default));

        Assert.Equal(["AER-1"], result.Changed);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task ABulkEditNamingMoreIssuesThanAnyoneMeantTo_IsRefused()
    {
        var h = await NewAsync();
        var keys = Enumerable.Range(1, 501).Select(n => $"AER-{n}").ToList();

        var result = await h.Issues.BulkEdit(Bulk(keys, statusId: h.Todo), default);

        Assert.Contains("at most 500", Reason(result.Result));
    }

    // ---- Harness ----

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    // ---- The claim, on every read a client draws from ----

    [Fact]
    public async Task ALiveClaim_RidesTheIssueAndTheCardAndTheSearch()
    {
        var h = await NewAsync();
        var issue = await h.CreateAsync("story", "somebody is on it");
        await h.ClaimAsync(issue.Key, "aerie-hatch", "somewhere:/checkouts/one", "running the tests");

        var read = Value(await h.Issues.GetIssue(issue.Key, default));
        Assert.NotNull(read.Claim);
        Assert.Equal("aerie-hatch", read.Claim.ClaimedBy);
        Assert.Equal("somewhere:/checkouts/one", read.Claim.Runner);
        Assert.Equal("running the tests", read.Claim.Chatter);

        // The lease rides the read, not only the take. A client is handed the
        // TTL because it is what "last heard from four minutes ago" has to be
        // judged against: four minutes into a five-minute lease is a runner
        // about to go, and four minutes into an hour is one that is fine.
        Assert.Equal(TestClaims.Ttl, read.Claim.TtlSeconds);

        // The board and the search hand out cards rather than issues, and a
        // claim a client cannot see on the screen it lives on is a claim it
        // cannot draw.
        var card = Value(await h.Board.GetBoard(default)).Issues.Single(c => c.Key == issue.Key);
        Assert.NotNull(card.Claim);
        Assert.Equal(TestClaims.Ttl, card.Claim.TtlSeconds);

        var found = Value(await h.Issues.SearchIssues(null, null, null, null, null, "somebody is on it", default));
        Assert.NotNull(Assert.Single(found).Claim);
    }

    [Fact]
    public async Task AClaim_NeverCarriesItsToken()
    {
        var h = await NewAsync();
        var issue = await h.CreateAsync("story", "held");
        var token = await h.ClaimAsync(issue.Key);

        // The token is a capability, not a fact about the issue. A read that
        // carried it would let anybody holding a board read steal or refresh
        // somebody else's lease - so it is on no read anywhere, and the way to
        // test that is to look at what the whole payload serializes to.
        var payload = System.Text.Json.JsonSerializer.Serialize(Value(await h.Issues.GetIssue(issue.Key, default)));

        Assert.DoesNotContain(token.ToString(), payload);
        Assert.Contains("aerie-hatch", payload);
    }

    [Fact]
    public async Task AnExpiredClaim_RidesNothing()
    {
        var h = await NewAsync();
        var issue = await h.CreateAsync("story", "its runner died");
        await h.ClaimAsync(issue.Key);

        h.Time.Advance(TimeSpan.FromSeconds(TestClaims.Ttl + 1));

        // Null rather than a claim with an old heartbeat: the arithmetic is the
        // server's, and no client should have to redo it.
        Assert.Null(Value(await h.Issues.GetIssue(issue.Key, default)).Claim);
        Assert.Null(Value(await h.Board.GetBoard(default)).Issues.Single(c => c.Key == issue.Key).Claim);
    }

    private sealed class Harness
    {
        public required HatchContext Db { get; init; }
        public required FakeTimeProvider Time { get; init; }

        /// <summary>Who the house knows. Empty until a test says otherwise, which reads as "nobody is assigned to anything".</summary>
        public required StubActorDirectory Actors { get; init; }
        public required IssuesController Issues { get; init; }
        public required IssueThreadController Thread { get; init; }
        public required QuestionsController Questions { get; init; }
        public required BoardController Board { get; init; }
        public required ProjectsController Projects { get; init; }
        public required StatusesController Statuses { get; init; }
        public required int ProjectId { get; init; }
        public required int OtherProjectId { get; init; }
        public required int Inbox { get; init; }
        public required int Todo { get; init; }
        public required int Done { get; init; }

        /// <summary>
        /// A lease, written straight onto the row - what the claim endpoints
        /// accept and refuse is IssueClaimTests' business, and these tests are
        /// about what a read hands out once one is there.
        /// </summary>
        public async Task<Guid> ClaimAsync(
            string key, string by = "aerie-hatch", string runner = "somewhere:/checkouts/one",
            string? chatter = null)
        {
            var token = Guid.NewGuid();
            var now = Time.GetUtcNow();
            var issue = await Db.Issues.Include(i => i.Project)
                .FirstAsync(i => i.Project!.Key + "-" + i.Number == key);

            issue.ClaimToken = token;
            issue.ClaimedBy = by;
            issue.ClaimRunner = runner;
            issue.ClaimedAt = now;
            issue.ClaimHeartbeatAt = now;
            issue.ClaimChatter = chatter;
            issue.ClaimChatterAt = chatter is null ? null : now;
            await Db.SaveChangesAsync();

            return token;
        }

        public async Task<IssueDto> CreateAsync(
            string type,
            string title,
            string? parentKey = null,
            int? projectId = null,
            string? readyAt = null,
            string? dueAt = null)
        {
            var result = await Issues.CreateIssue(
                new IssueCreateRequest(projectId ?? ProjectId, type, title, null, parentKey, readyAt, dueAt), default);

            return Created(result);
        }

        /// <summary>
        /// An issue given to a person, written straight to the column - what
        /// the route that writes one accepts and refuses is
        /// <see cref="AssigneeControllerTests"/>'s business, and these tests are
        /// about what the two reads make of it once it is set. An id the
        /// directory does not know is how a deleted person is written here.
        /// </summary>
        public async Task AssignAsync(string key, Guid personId)
        {
            IssueKey.TryParse(key, out var projectKey, out var number);
            var issue = await Db.Issues.WithKey(projectKey, number).FirstAsync();
            issue.AssigneePersonId = personId;
            await Db.SaveChangesAsync();
        }

        public async Task<IReadOnlyList<IssueEventDto>> EventsAsync(string key) =>
            Value(await Thread.GetEvents(key, default));

        /// <summary>A question on an issue, as an agent would ask one.</summary>
        public async Task<CommentDto> AskAsync(string key, string body) =>
            Created(await Thread.AddComment(key, new CommentCreateRequest(body, EfHatchComment.Question), default));

        /// <summary>The answer to one, as a person would give it.</summary>
        public async Task<CommentDto> AnswerAsync(string key, long questionId, string body) =>
            Created(await Thread.AddComment(key, new CommentCreateRequest(body, EfHatchComment.Answer, questionId), default));

        /// <summary>The keys a filter finds, in the order the endpoint returns them.</summary>
        public async Task<IReadOnlyList<string>> SearchAsync(
            int? projectId = null,
            string? type = null,
            int? statusId = null,
            string? parentKey = null,
            string? ancestorKey = null,
            string? text = null)
        {
            var result = await Issues.SearchIssues(projectId, type, statusId, parentKey, ancestorKey, text, default);
            return Value(result).Select(i => i.Key).ToList();
        }
    }

    private static async Task<Harness> NewAsync()
    {
        var db = new HatchContext(
            new DbContextOptionsBuilder<HatchContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var aerie = new EfHatchProject { Key = "AER", Name = "Aerie", CreatedAt = Now };
        var ops = new EfHatchProject { Key = "OPS", Name = "Operations", CreatedAt = Now };
        var inbox = new EfHatchStatus { Name = "inbox", SortOrder = 10 };
        var todo = new EfHatchStatus { Name = "todo", SortOrder = 20 };
        var done = new EfHatchStatus { Name = "done", SortOrder = 40, IsTerminal = true };
        db.AddRange(aerie, ops, inbox, todo, done);
        await db.SaveChangesAsync();

        var time = new FakeTimeProvider(Now);
        var caller = new StubCallerIdentity { Person = new EfPerson { Name = "Nathan", CreatedAt = Now, UpdatedAt = Now } };
        var ranks = new RankService(db);
        var actors = new StubActorDirectory();

        return new Harness
        {
            Db = db,
            Time = time,
            Actors = actors,
            Issues = new IssuesController(db, ranks, actors, TestClaims.With(), caller, time),
            Thread = new IssueThreadController(db, caller, time),
            Questions = new QuestionsController(db),
            Board = new BoardController(db, actors, TestClaims.With(), time),
            Projects = new ProjectsController(db, time),
            Statuses = new StatusesController(db),
            ProjectId = aerie.Id,
            OtherProjectId = ops.Id,
            Inbox = inbox.Id,
            Todo = todo.Id,
            Done = done.Id,
        };
    }

    /// <summary>Whoever the test says is holding the phone. Their name is the audit actor.</summary>
    private sealed class StubCallerIdentity : ICallerIdentity
    {
        public EfPerson? Person { get; set; }

        /// <summary>The key this test says is calling, when it is a program rather than a person.</summary>
        public EfApiKey? Key { get; set; }

        public Task<EfAuthGrant?> GrantAsync(CancellationToken ct) => Task.FromResult<EfAuthGrant?>(null);

        public Task<Guid?> PersonIdAsync(CancellationToken ct) => Task.FromResult(Person?.Id);

        public Task<EfPerson?> PersonAsync(CancellationToken ct) => Task.FromResult(Person);

        public Task<EfApiKey?> ApiKeyAsync(CancellationToken ct) => Task.FromResult(Key);

        /// <summary>Local mode's third lane: the person at the machine, or a runner that named itself. Null is every install with a wall.</summary>
        public Actor? Local { get; set; }

        public Task<Actor?> LocalAsync(CancellationToken ct) => Task.FromResult(Local);

        public Task<bool> IsProgramAsync(CancellationToken ct) =>
            Task.FromResult(Key is not null || Local is { Kind: ActorKind.Key });

        public Task<string> ActorNameAsync(CancellationToken ct) =>
            Task.FromResult(Person?.Name ?? Key?.Name ?? Local?.Name ?? CallerIdentity.Unattributed);
    }

    private static IssuePatchRequest Patch(
        string? title = null,
        string? description = null,
        string? type = null,
        int? statusId = null,
        string? parentKey = null,
        string? readyAt = null,
        string? dueAt = null,
        string? pullRequestUrl = null) =>
        new(title, description, type, statusId, parentKey, readyAt, dueAt, pullRequestUrl);

    private static IssueBulkEditRequest Bulk(
        IReadOnlyList<string> keys,
        string? type = null,
        int? statusId = null,
        string? parentKey = null,
        string? readyAt = null,
        string? dueAt = null) =>
        new(keys, type, statusId, parentKey, readyAt, dueAt);

    private static T Value<T>(ActionResult<T> result) =>
        result.Value ?? throw new InvalidOperationException($"expected a value, got {Reason(result.Result)}");

    private static T Created<T>(ActionResult<T> result) =>
        result.Result is CreatedAtActionResult created
            ? (T)created.Value!
            : result.Value ?? throw new InvalidOperationException($"expected a created value, got {Reason(result.Result)}");

    /// <summary>The plain-text reason on a refusal - what the UI puts on screen.</summary>
    private static string Reason(IActionResult? result) => result switch
    {
        ObjectResult o => $"{o.StatusCode}: {o.Value}",
        StatusCodeResult s => s.StatusCode.ToString(),
        null => "no result",
        _ => result.GetType().Name,
    };
}

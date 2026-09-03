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
    public async Task AProjectKey_IsNotOfferedForEditingAndTheNameIs()
    {
        var h = await NewAsync();

        var patched = Value(await h.Projects.PatchProject(h.ProjectId, new ProjectPatchRequest("The House"), default));

        Assert.Equal("The House", patched.Name);
        Assert.Equal("AER", patched.Key);
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

    // ---- Harness ----

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public required HatchContext Db { get; init; }
        public required IssuesController Issues { get; init; }
        public required IssueThreadController Thread { get; init; }
        public required BoardController Board { get; init; }
        public required ProjectsController Projects { get; init; }
        public required StatusesController Statuses { get; init; }
        public required int ProjectId { get; init; }
        public required int OtherProjectId { get; init; }
        public required int Inbox { get; init; }
        public required int Todo { get; init; }
        public required int Done { get; init; }

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

        public async Task<IReadOnlyList<IssueEventDto>> EventsAsync(string key) =>
            Value(await Thread.GetEvents(key, default));
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

        return new Harness
        {
            Db = db,
            Issues = new IssuesController(db, ranks, caller, time),
            Thread = new IssueThreadController(db, caller, time),
            Board = new BoardController(db),
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

        public Task<string> ActorNameAsync(CancellationToken ct) =>
            Task.FromResult(Person?.Name ?? Key?.Name ?? CallerIdentity.Unattributed);
    }

    private static IssuePatchRequest Patch(
        string? title = null,
        string? description = null,
        string? type = null,
        int? statusId = null,
        string? parentKey = null,
        string? readyAt = null,
        string? dueAt = null) =>
        new(title, description, type, statusId, parentKey, readyAt, dueAt);

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

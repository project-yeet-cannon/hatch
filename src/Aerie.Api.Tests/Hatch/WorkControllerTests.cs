using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Modules.Hatch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// What an unattended run picks up, and what it is told to do with it.
///
/// These are the rules that decide how an agent spends money and what it is
/// allowed to change, so each one is pinned rather than trusted to a prompt:
/// the board is worked right to left, a terminal column is never an agent's to
/// enter, and a transition with no playbook dispatches nothing at all.
/// </summary>
public class WorkControllerTests
{
    // ---- Picking ----

    [Fact]
    public async Task NextWork_TakesTheRightmostColumnBeforeTheLeftmost()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "sitting in todo", h.Todo);
        var advanced = await h.FileAsync("story", "already underway", h.InProgress);

        var work = Value(await h.Work.GetNextWork(0, null, null, default));

        // Both are workable. The one nearer the end of the board wins, because
        // a board worked left to right starts everything and finishes nothing.
        Assert.Equal(Key(advanced), work.Issue.Key);
        Assert.Equal("review", work.ToStatus!.Name);
    }

    [Fact]
    public async Task NextWork_TakesTheTopOfTheColumn()
    {
        var h = await NewAsync();
        var first = await h.FileAsync("story", "top", h.Todo, rank: 1024);
        await h.FileAsync("story", "below it", h.Todo, rank: 2048);

        Assert.Equal(Key(first), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);
    }

    [Fact]
    public async Task NextWork_SkipsAnIssueWhoseReadyDateHasNotArrived()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "waiting on a renewal", h.Todo, rank: 1024, readyAt: Now.AddDays(3));
        var workable = await h.FileAsync("story", "can start now", h.Todo, rank: 2048);

        // The folded card is above it and is passed over anyway - the board
        // hides it for the same reason (schedule.ts).
        Assert.Equal(Key(workable), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);
    }

    [Fact]
    public async Task NextWork_TakesAnIssueReadyLaterToday()
    {
        var h = await NewAsync();
        var today = await h.FileAsync("story", "ready at five", h.Todo, readyAt: Now.AddHours(5));

        // Ready from the start of the day it names, whatever hour was set: a
        // ticket that becomes workable at 5pm is not one nobody may look at
        // over breakfast.
        Assert.Equal(Key(today), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);
    }

    [Fact]
    public async Task NextWork_SaysNothingWhenTheOnlyWorkLeftIsTheOperatorsToJudge()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "waiting on a human", h.Review);
        await h.FileAsync("story", "shipped", h.Done);

        // Review's only exit is terminal and done has no exit at all, so an
        // unattended run has nothing it may do - which is a 204, not a card.
        Assert.IsType<NoContentResult>((await h.Work.GetNextWork(0, null, null, default)).Result);
    }

    // ---- One corner of the board ----

    [Fact]
    public async Task NextWork_UnderAnEpic_LeavesTheRestOfTheBoardAlone()
    {
        var h = await NewAsync();
        var mine = await h.FileAsync("epic", "the one I am pushing", h.Todo, rank: 4096);
        var story = await h.FileAsync("story", "under mine", h.Todo, rank: 2048, parentId: mine.Id);
        var elsewhere = await h.FileAsync("story", "another epic's, and above it", h.Todo, rank: 1024);

        // Unscoped this is the top of the column and would win outright.
        Assert.Equal(Key(elsewhere), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);

        Assert.Equal(Key(story), Value(await h.Work.GetNextWork(0, Key(mine), null, default)).Issue.Key);
    }

    [Fact]
    public async Task NextWork_UnderAnEpic_ReachesAStoryTwoLevelsDown()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the epic", h.Todo, rank: 4096);
        var under = await h.FileAsync("epic", "an epic inside it", h.Todo, rank: 2048, parentId: epic.Id);
        var story = await h.FileAsync("story", "the story", h.InProgress, rank: 1024, parentId: under.Id);

        // Right to left still decides inside the scope: the story is further
        // along than the epic above it, and depth has nothing to do with it.
        Assert.Equal(Key(story), Value(await h.Work.GetNextWork(0, Key(epic), null, default)).Issue.Key);
    }

    [Fact]
    public async Task NextWork_UnderAnEpic_PassesOverABlockedChildExactlyAsTheWholeBoardDoes()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the epic", h.Todo, rank: 4096);
        var asked = await h.FileAsync("story", "waiting on a decision", h.Todo, rank: 1024, parentId: epic.Id);
        var workable = await h.FileAsync("story", "nothing in its way", h.Todo, rank: 2048, parentId: epic.Id);
        await h.AskAsync(asked, "per-node or global?");

        // The scope narrows the candidates and decides nothing about them.
        Assert.Equal(Key(workable), Value(await h.Work.GetNextWork(0, Key(epic), null, default)).Issue.Key);
    }

    [Fact]
    public async Task NextWork_UnderAnEpic_DoesNotOfferTheEpicItself()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "workable, and not the question", h.Todo);

        // "Under AER-1" is a question about what hangs beneath it. Asked of
        // the whole board this epic is the only thing on it and is the answer;
        // asked of itself it is not a candidate for its own scope.
        Assert.Equal(Key(epic), Value(await h.Work.GetNextWork(0, null, "epic", default)).Issue.Key);
        Assert.IsType<NoContentResult>((await h.Work.GetNextWork(0, Key(epic), "epic", default)).Result);
    }

    [Fact]
    public async Task NextWork_UnderAnEpicWithNothingToDo_IsThe204AnEmptyBoardGives()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "finished", h.Todo, rank: 4096);
        await h.FileAsync("story", "shipped", h.Done, rank: 2048, parentId: epic.Id);
        await h.FileAsync("story", "somebody else's problem", h.Todo, rank: 1024);

        // Not a failure - `hatch.sh` reads it as "nothing to do", which is what
        // it means whether the scope is one epic or the whole tracker.
        Assert.IsType<NoContentResult>((await h.Work.GetNextWork(0, Key(epic), null, default)).Result);
    }

    [Fact]
    public async Task NextWork_UnderAKeyNobodyMinted_IsTheSentenceTheSearchEndpointUses()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "workable", h.Todo);

        var refused = Assert.IsType<BadRequestObjectResult>((await h.Work.GetNextWork(0, "AER-999", null, default)).Result);

        // A scope nobody can name is a typo, not an empty subtree, and it must
        // not read as "the work has run out".
        Assert.Equal("there is no AER-999", refused.Value);
    }

    // ---- What the loop may pick up ----
    //
    // The two rules that are the loop's policy rather than facts about an
    // issue. Both are `next`-only: a person who names a ticket is giving an
    // instruction, and housekeeping does not overrule it.

    [Fact]
    public async Task NextWork_PassesOverAnEpicAndATaskAtTheTopOfTheColumn()
    {
        var h = await NewAsync();
        await h.FileAsync("epic", "somebody has to choose what this contains", h.Todo, rank: 1024);
        await h.FileAsync("task", "a seam inside a story", h.Todo, rank: 2048);
        var story = await h.FileAsync("story", "the unit that ships", h.Todo, rank: 4096);

        // Both of the others are above it and workable. An unattended run takes
        // neither: an epic is a product call and a task moves as part of the
        // story it is a seam in.
        Assert.Equal(Key(story), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);
    }

    [Fact]
    public async Task NextWork_TakesTheTypesTheCallerNames()
    {
        var h = await NewAsync();
        var task = await h.FileAsync("task", "a seam inside a story", h.Todo, rank: 1024);
        await h.FileAsync("story", "the unit that ships", h.Todo, rank: 2048);

        // An operator who means to have an evening spent on tasks says so, and
        // the top of the column is the top of the column again.
        var work = Value(await h.Work.GetNextWork(0, null, "story,bug,task", default));

        Assert.Equal(Key(task), work.Issue.Key);
    }

    [Fact]
    public async Task NextWork_RefusesATypeNobodyDefined()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "workable", h.Todo);

        var refused = Assert.IsType<BadRequestObjectResult>(
            (await h.Work.GetNextWork(0, null, "story,epci", default)).Result);

        // A misspelling that quietly matched nothing would read as a finished
        // board, which is the one answer a loop acts on.
        Assert.Contains("epci", (string)refused.Value!);
    }

    [Fact]
    public async Task NextWork_PassesOverAnIssueWhoseSiblingIsAwaitingReview()
    {
        var h = await NewAsync();
        var mine = await h.FileAsync("epic", "the effort", h.Todo, rank: 8192);
        await h.FileAsync("story", "already up for review", h.Review, rank: 1024, parentId: mine.Id);
        await h.FileAsync("story", "the next one under it", h.Todo, rank: 1024, parentId: mine.Id);

        var other = await h.FileAsync("epic", "another effort", h.Todo, rank: 8192);
        var elsewhere = await h.FileAsync("story", "under nothing in flight", h.Todo, rank: 2048, parentId: other.Id);

        // The folded one is above it in the column: two open pull requests
        // under one parent is one too many, so the loop opens the other one.
        Assert.Equal(Key(elsewhere), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);
    }

    [Fact]
    public async Task NextWork_DoesNotTreatTwoParentlessIssuesAsSiblings()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "up for review, under nothing", h.Review, rank: 1024);
        var workable = await h.FileAsync("story", "also under nothing", h.Todo, rank: 1024);

        // A null parent is not a group. Otherwise one loose story in review
        // would stop every other loose story on the board.
        Assert.Equal(Key(workable), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);
    }

    [Fact]
    public async Task NextWork_IsNotHeldUpByASiblingThatShipped()
    {
        var h = await NewAsync();
        var mine = await h.FileAsync("epic", "the effort", h.Todo, rank: 8192);
        await h.FileAsync("story", "merged last week", h.Done, rank: 1024, parentId: mine.Id);
        var workable = await h.FileAsync("story", "the next one under it", h.Todo, rank: 2048, parentId: mine.Id);

        // The rule is about work in flight, and merged work is not in flight.
        Assert.Equal(Key(workable), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);
    }

    [Fact]
    public async Task Work_OnANamedIssueIgnoresTheLoopsPolicy()
    {
        var h = await NewAsync();
        var parent = await h.FileAsync("epic", "the effort", h.Todo, rank: 8192);
        await h.FileAsync("story", "already up for review", h.Review, rank: 1024, parentId: parent.Id);
        var epic = await h.FileAsync("epic", "a type the loop does not take", h.Todo, rank: 2048, parentId: parent.Id);

        // Nothing here is an unattended run's to start...
        Assert.IsType<NoContentResult>((await h.Work.GetNextWork(0, null, null, default)).Result);

        // ...and all of it is somebody's to ask for by name. Neither the type
        // nor the sibling is a fact about this issue, so neither refuses it.
        Assert.Null(Value(await h.Work.GetWork(Key(epic), default)).Blocked);
    }

    // ---- Refusals ----

    [Fact]
    public async Task Work_RefusesToEnterATerminalColumn()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync("task", "ready for a verdict", h.Review);

        var work = Value(await h.Work.GetWork(Key(issue), default));

        Assert.Equal("done", work.ToStatus!.Name);
        Assert.Contains("only the operator", work.Blocked);
    }

    [Fact]
    public async Task Work_RefusesWhenThereIsNoPlaybookForTheTransition()
    {
        var h = await NewAsync();
        h.Db.Playbooks.RemoveRange(h.Db.Playbooks.Where(p => p.FromStatusId == h.Todo));
        await h.Db.SaveChangesAsync();
        var issue = await h.FileAsync("story", "specified, undispatchable", h.Todo);

        var work = Value(await h.Work.GetWork(Key(issue), default));

        Assert.Contains("no playbook covers", work.Blocked);
        Assert.Null(work.Playbook);
    }

    [Fact]
    public async Task Work_OnANamedIssueAnswersEvenWhenItIsBlocked()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync("task", "shipped", h.Done);

        // A person who asked for this key is owed the sentence saying why it
        // cannot move, not a 404.
        var work = Value(await h.Work.GetWork(Key(issue), default));

        Assert.Equal(Key(issue), work.Issue.Key);
        Assert.Null(work.ToStatus);
        Assert.NotNull(work.Blocked);
    }

    [Fact]
    public async Task Work_RefusesAnIssueHoldingAnUnansweredQuestion()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync("task", "asked and waiting", h.Todo);
        await h.AskAsync(issue, "per-node or global?");

        var work = Value(await h.Work.GetWork(Key(issue), default));

        // Not a missing playbook and not a terminal column: this one is waiting
        // on a person, and dispatching at it would produce a second session
        // asking the same thing or guessing at the answer.
        Assert.Contains("unanswered question", work.Blocked);
        Assert.NotNull(work.Playbook);
    }

    [Fact]
    public async Task NextWork_PassesOverAnIssueWaitingOnAnAnswer()
    {
        var h = await NewAsync();
        var asked = await h.FileAsync("story", "waiting on a decision", h.Todo, rank: 1024);
        var workable = await h.FileAsync("story", "nothing in its way", h.Todo, rank: 2048);
        await h.AskAsync(asked, "per-node or global?");

        // Folded past exactly as a card whose ready date has not arrived is,
        // and for the same reason: it is not workable yet.
        Assert.Equal(Key(workable), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);
    }

    [Fact]
    public async Task Work_DispatchesOnceTheQuestionHasBeenAnswered()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync("task", "asked and answered", h.Todo);
        var question = await h.AskAsync(issue, "per-node or global?");
        await h.AnswerAsync(issue, question, "per-node");

        var work = Value(await h.Work.GetWork(Key(issue), default));

        Assert.Null(work.Blocked);
    }

    [Fact]
    public async Task Work_CarriesTheAnsweredQuestionsSoTheNextSessionDoesNotReopenThem()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync("task", "decided", h.Todo);
        var question = await h.AskAsync(issue, "per-node or global?");
        await h.AnswerAsync(issue, question, "per-node");

        var work = Value(await h.Work.GetWork(Key(issue), default));

        var carried = Assert.Single(work.Questions);
        Assert.Equal("per-node or global?", carried.Body);
        Assert.Equal("per-node", Assert.Single(carried.Answers).Body);
    }

    // ---- Matching ----

    [Fact]
    public async Task Work_PrefersThePlaybookThatNamesTheType()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "a big one", h.Todo);
        var task = await h.FileAsync("task", "a small one", h.Todo);

        Assert.Equal("opus", Value(await h.Work.GetWork(Key(epic), default)).Playbook!.Model);
        Assert.Equal("sonnet", Value(await h.Work.GetWork(Key(task), default)).Playbook!.Model);
    }

    [Fact]
    public async Task Work_FallsBackToTheCatchAllForATypeNobodyNamed()
    {
        var h = await NewAsync();
        var bug = await h.FileAsync("bug", "unnamed by any specific row", h.Todo);

        var playbook = Value(await h.Work.GetWork(Key(bug), default)).Playbook;

        Assert.NotNull(playbook);
        Assert.Empty(playbook.Types);
    }

    [Fact]
    public async Task Work_CarriesTheChildrenSoTheAgentCanDescend()
    {
        var h = await NewAsync();
        var story = await h.FileAsync("story", "parent", h.Todo);
        await h.FileAsync("task", "first", h.Todo, parentId: story.Id);
        await h.FileAsync("task", "second", h.Todo, parentId: story.Id);

        var work = Value(await h.Work.GetWork(Key(story), default));

        Assert.Equal(["first", "second"], work.Children.Select(c => c.Title));
    }

    // ---- The scan ----
    //
    // What a whole pass would do, rather than what its first step is. These
    // pin the two properties the endpoint exists for: it is the same walk
    // `next` takes, and every fold it makes says why.

    [Fact]
    public async Task Queue_ReportsTheBoardInTheOrderTheDispatcherWalksIt()
    {
        var h = await NewAsync();
        var filed = await h.FileAsync("story", "in the inbox", h.Inbox);
        var waiting = await h.FileAsync("story", "below the top of todo", h.Todo, rank: 2048);
        var top = await h.FileAsync("story", "top of todo", h.Todo, rank: 1024);
        var underway = await h.FileAsync("story", "underway", h.InProgress);
        var judged = await h.FileAsync("story", "awaiting the operator", h.Review);

        var queue = Value(await h.Work.GetQueue(0, null, null, default));

        // Rightmost column first, then top of the column down - the scheduling
        // policy, written out rather than implied by which card came back.
        Assert.Equal(
            new[] { judged, underway, top, waiting, filed }.Select(Key),
            queue.Select(e => e.Issue.Key));
    }

    [Fact]
    public async Task Queue_LeavesOutTheColumnsWithNowhereToGo()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "shipped", h.Done);
        var live = await h.FileAsync("story", "still going", h.Todo);

        // Done is terminal, so the dispatcher never reaches it. An issue it
        // never reaches is not one the pass skipped, and shipped work is not a
        // backlog.
        Assert.Equal([Key(live)], Value(await h.Work.GetQueue(0, null, null, default)).Select(e => e.Issue.Key));
    }

    [Fact]
    public async Task Queue_FirstClearEntryIsWhatNextReturns()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "awaiting the operator", h.Review);
        await h.FileAsync("epic", "not a type the loop takes", h.Todo, rank: 1024);
        await h.FileAsync("story", "waiting on a renewal", h.Todo, rank: 2048, readyAt: Now.AddDays(3));
        await h.FileAsync("story", "the one it should take", h.Todo, rank: 3072);
        await h.FileAsync("story", "below it", h.Todo, rank: 4096);

        var queue = Value(await h.Work.GetQueue(0, null, null, default));
        var work = Value(await h.Work.GetNextWork(0, null, null, default));

        // The property the endpoint exists for: one walk, reported and acted
        // on. Two loops that could disagree about the order of the board is
        // precisely the bug the scan is here to expose.
        Assert.Equal(queue.First(e => e.Blocked is null).Issue.Key, work.Issue.Key);
    }

    [Fact]
    public async Task Queue_AgreesWithNextWhenThereIsNothingToDo()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "awaiting the operator", h.Review);
        await h.FileAsync("task", "a seam inside a story", h.Todo);

        var queue = Value(await h.Work.GetQueue(0, null, null, default));

        Assert.All(queue, e => Assert.NotNull(e.Blocked));
        Assert.IsType<NoContentResult>((await h.Work.GetNextWork(0, null, null, default)).Result);
    }

    [Fact]
    public async Task Queue_OnAnEmptyBoardIsEmptyRatherThanRefused()
    {
        var h = await NewAsync();

        Assert.Empty(Value(await h.Work.GetQueue(0, null, null, default)));
    }

    // ---- ...and every fold it makes, named ----

    [Fact]
    public async Task Queue_NamesAReadyDateThatHasNotArrived()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "waiting on a renewal", h.Todo, readyAt: Now.AddDays(3));

        Assert.Contains("not workable until", Only(await h.Work.GetQueue(0, null, null, default)).Blocked);
    }

    [Fact]
    public async Task Queue_NamesAnUnansweredQuestion()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync("story", "waiting on a person", h.Todo);
        await h.AskAsync(issue, "how should retries be scoped?");

        Assert.Contains("unanswered question", Only(await h.Work.GetQueue(0, null, null, default)).Blocked);
    }

    [Fact]
    public async Task Queue_NamesATypeTheLoopDoesNotPickUp()
    {
        var h = await NewAsync();
        await h.FileAsync("epic", "somebody's product call", h.Todo);

        // The case the epic's source note names: work sitting in a column an
        // unattended run walks past, said out loud instead of silently folded.
        Assert.Equal(
            "an epic is not a type an unattended run picks up",
            Only(await h.Work.GetQueue(0, null, null, default)).Blocked);
    }

    [Fact]
    public async Task Queue_StopsNamingATypeTheCallerAsksFor()
    {
        var h = await NewAsync();
        await h.FileAsync("epic", "somebody's product call", h.Todo);

        // Widened by the caller, so it is no longer a reason - and the epic
        // playbook covers todo to in progress, so nothing else refuses it.
        Assert.Null(Only(await h.Work.GetQueue(0, null, "epic", default)).Blocked);
    }

    [Fact]
    public async Task Queue_NamesTheSiblingThatIsAlreadyAwaitingReview()
    {
        var h = await NewAsync();
        var parent = await h.FileAsync("epic", "the effort", h.Todo);
        var inFlight = await h.FileAsync("story", "already up for review", h.Review, parentId: parent.Id);
        var held = await h.FileAsync("story", "held back", h.Todo, parentId: parent.Id);

        var blocked = Value(await h.Work.GetQueue(0, null, null, default))
            .Single(e => e.Issue.Key == Key(held)).Blocked;

        Assert.Contains(Key(inFlight), blocked);
        Assert.Contains("two open pull requests under one parent", blocked);
    }

    [Fact]
    public async Task Queue_NamesAColumnAndTypeNoPlaybookCovers()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "somebody's paragraph", h.Inbox);

        // Nothing in the seeded matrix speaks for inbox to todo here, which is
        // the difference between "no work left" and "no instructions left" -
        // and a loop that could not tell them apart would report a finished
        // board every night.
        Assert.Equal(
            "no playbook covers \"inbox\" to \"todo\" for a story - add one on the Playbooks page",
            Only(await h.Work.GetQueue(0, null, null, default)).Blocked);
    }

    [Fact]
    public async Task Queue_NamesTheNextColumnBeingTerminal()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "awaiting the operator", h.Review);

        Assert.Equal(
            "the next column is \"done\", and only the operator moves work there",
            Only(await h.Work.GetQueue(0, null, null, default)).Blocked);
    }

    [Fact]
    public async Task Queue_SaysTheBoardIsTheReasonBeforeItSaysTheTypeIs()
    {
        var h = await NewAsync();
        await h.FileAsync("epic", "an epic the operator must judge", h.Review);

        // Both are true. The one that no argument can change is the one worth
        // printing: widening --types would not make this issue movable.
        Assert.Contains("only the operator moves work there", Only(await h.Work.GetQueue(0, null, null, default)).Blocked);
    }

    // ---- One corner of the board, scanned ----

    [Fact]
    public async Task Queue_UnderAnEpic_LeavesTheRestOfTheBoardAlone()
    {
        var h = await NewAsync();
        var mine = await h.FileAsync("epic", "the one I am pushing", h.Todo);
        var story = await h.FileAsync("story", "under mine", h.Todo, parentId: mine.Id);
        await h.FileAsync("story", "somebody else's", h.Todo);

        // The same reading of ancestorKey the search filter and the meters
        // use: what hangs beneath the key, and not the key itself.
        Assert.Equal([Key(story)], Value(await h.Work.GetQueue(0, Key(mine), null, default)).Select(e => e.Issue.Key));
    }

    [Fact]
    public async Task Queue_UnderAKeyNobodyMinted_IsTheSentenceTheSearchEndpointUses()
    {
        var h = await NewAsync();

        var refusal = Assert.IsType<BadRequestObjectResult>((await h.Work.GetQueue(0, "AER-404", null, default)).Result);
        Assert.Equal("there is no AER-404", refusal.Value);
    }

    [Fact]
    public async Task Queue_RefusesATypeNobodyDefined()
    {
        var h = await NewAsync();

        var refusal = Assert.IsType<BadRequestObjectResult>((await h.Work.GetQueue(0, null, "stroy", default)).Result);
        Assert.Contains("there is no \"stroy\" type", (string)refusal.Value!);
    }

    [Fact]
    public async Task Queue_CarriesTheTransitionAnIssueIsClearFor()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "ready to go", h.Todo);

        var entry = Only(await h.Work.GetQueue(0, null, null, default));

        // What a terminal prints on the line where there is no reason: where
        // this issue is, and where the increment would leave it.
        Assert.Null(entry.Blocked);
        Assert.Equal("todo", entry.FromStatus.Name);
        Assert.Equal("in progress", entry.ToStatus!.Name);
    }

    // ---- The one edge that is cut ----

    [Fact]
    public void WritingAPlaybook_IsClosedToAnApiKey()
    {
        var writes = new[] { nameof(PlaybooksController.CreatePlaybook), nameof(PlaybooksController.PatchPlaybook), nameof(PlaybooksController.DeletePlaybook) };

        foreach (var name in writes)
        {
            var guard = typeof(PlaybooksController).GetMethod(name)!
                .GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false)
                .Cast<RequireAdminAttribute>()
                .SingleOrDefault();

            // No scope named is the whole point: an agent that could widen its
            // own prompt and raise its own effort has no fixed point to settle
            // at. The refusal is a property of the route, not of a prompt
            // asking nicely.
            Assert.NotNull(guard);
            Assert.Null(guard.AcceptScope);
        }
    }

    [Fact]
    public void ReadingAPlaybook_IsOpenToAnApiKey()
    {
        var guard = typeof(PlaybooksController).GetMethod(nameof(PlaybooksController.GetPlaybooks))!
            .GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false)
            .Cast<RequireAdminAttribute>()
            .Single();

        // An agent has to be able to read what it was dispatched with.
        Assert.Equal(ApiKeyScopes.Hatch, guard.AcceptScope);
    }

    // ---- Harness ----

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public required HatchContext Db { get; init; }
        public required WorkController Work { get; init; }
        public required PlaybooksController Playbooks { get; init; }
        public required int ProjectId { get; init; }
        public required int Inbox { get; init; }
        public required int Todo { get; init; }
        public required int InProgress { get; init; }
        public required int Review { get; init; }
        public required int Done { get; init; }

        private int next = 1;

        /// <summary>
        /// An issue placed directly, rather than through the create endpoint -
        /// these tests are about where work is picked up from, so the column
        /// and the rank are the inputs and the create path is not under test.
        /// </summary>
        public async Task<EfHatchIssue> FileAsync(
            string type, string title, int statusId,
            long rank = 1024, DateTimeOffset? readyAt = null, long? parentId = null)
        {
            var issue = new EfHatchIssue
            {
                ProjectId = ProjectId,
                Number = next++,
                Type = type,
                Title = title,
                Description = "",
                StatusId = statusId,
                Rank = rank,
                ParentId = parentId,
                ReadyAt = readyAt,
                ReadyAtHasTime = readyAt is not null,
                CreatedBy = "operator",
                CreatedAt = Now,
                UpdatedAt = Now,
            };

            Db.Issues.Add(issue);
            await Db.SaveChangesAsync();
            return issue;
        }

        /// <summary>
        /// A question on an issue, written straight to the table - these tests
        /// are about what a question does to a dispatch, and the endpoint that
        /// writes one is covered where the rest of the comment rules are.
        /// </summary>
        public async Task<EfHatchComment> AskAsync(EfHatchIssue issue, string body)
        {
            var comment = new EfHatchComment
            {
                IssueId = issue.Id,
                Author = "hatch-agent",
                Body = body,
                Kind = EfHatchComment.Question,
                CreatedAt = Now,
            };

            Db.Comments.Add(comment);
            await Db.SaveChangesAsync();
            return comment;
        }

        public async Task AnswerAsync(EfHatchIssue issue, EfHatchComment question, string body)
        {
            Db.Comments.Add(new EfHatchComment
            {
                IssueId = issue.Id,
                Author = "operator",
                Body = body,
                Kind = EfHatchComment.Answer,
                AnswersId = question.Id,
                CreatedAt = Now,
            });

            await Db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// A board with the shape the Playbooks migration leaves behind, and the
    /// three playbook rows these tests reason about - a type-specific one, a
    /// catch-all beside it, and one for the column further right.
    /// </summary>
    private static async Task<Harness> NewAsync()
    {
        var db = new HatchContext(
            new DbContextOptionsBuilder<HatchContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var project = new EfHatchProject { Key = "AER", Name = "Aerie", CreatedAt = Now };
        var inbox = new EfHatchStatus { Name = "inbox", SortOrder = 10 };
        var todo = new EfHatchStatus { Name = "todo", SortOrder = 20 };
        var doing = new EfHatchStatus { Name = "in progress", SortOrder = 30 };
        var review = new EfHatchStatus { Name = "review", SortOrder = 35 };
        var done = new EfHatchStatus { Name = "done", SortOrder = 40, IsTerminal = true };
        db.AddRange(project, inbox, todo, doing, review, done);
        await db.SaveChangesAsync();

        db.AddRange(
            Playbook(todo.Id, doing.Id, "epic", "opus"),
            Playbook(todo.Id, doing.Id, "", "sonnet"),
            Playbook(doing.Id, review.Id, "", "sonnet"));
        await db.SaveChangesAsync();

        return new Harness
        {
            Db = db,
            Work = new WorkController(db, new FakeTimeProvider(Now)),
            Playbooks = new PlaybooksController(db, new FakeTimeProvider(Now)),
            ProjectId = project.Id,
            Inbox = inbox.Id,
            Todo = todo.Id,
            InProgress = doing.Id,
            Review = review.Id,
            Done = done.Id,
        };
    }

    /// <summary>
    /// The one row of a scan of a board with one issue on it - so that a test
    /// about a single fold says which fold and nothing about arithmetic.
    /// </summary>
    private static QueueEntryDto Only(ActionResult<IReadOnlyList<QueueEntryDto>> result) => Assert.Single(Value(result));

    /// <summary>The display key of an issue these tests filed directly.</summary>
    private static string Key(EfHatchIssue issue) => IssueKey.Format("AER", issue.Number);

    private static EfHatchPlaybook Playbook(int from, int to, string types, string model) => new()
    {
        FromStatusId = from,
        ToStatusId = to,
        Types = types,
        Prompt = "do the thing",
        Model = model,
        Effort = "high",
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static T Value<T>(ActionResult<T> result) =>
        result.Value ?? throw new InvalidOperationException($"expected a value, got {result.Result?.GetType().Name ?? "nothing"}");
}

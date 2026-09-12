using Hatch.Api.Common;
using Hatch.Api.Ef;
using Hatch.Api.Modules.Hatch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Hatch.Api.Tests.Hatch;

/// <summary>
/// What an unattended run takes off the board, and what it is told to do with
/// it.
///
/// These are the rules that decide how an agent spends money and what it is
/// allowed to change, so each one is pinned rather than trusted to a prompt:
/// the board is worked right to left, a terminal column is never an agent's to
/// enter, a transition with no playbook dispatches nothing at all, and which
/// types a move applies to is that playbook row's to say and nowhere else's.
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

    // ---- Whose work it is ----

    [Fact]
    public async Task NextWork_SkipsAnIssueAssignedToAPerson()
    {
        var h = await NewAsync();
        var ada = h.Actors.AddPerson("Ada");
        var hers = await h.FileAsync("story", "Ada is on this", h.Todo, rank: 1024);
        var workable = await h.FileAsync("story", "nobody's", h.Todo, rank: 2048);
        await h.AssignAsync(hers, personId: ada.Id);

        // A name on a ticket takes it off the night shift. The folded card is
        // above it and is passed over anyway, the same as a ready date.
        Assert.Equal(Key(workable), Value(await h.Work.GetNextWork(0, null, default)).Issue.Key);
    }

    [Fact]
    public async Task NextWork_TakesAnIssueAssignedToAnApiKey()
    {
        var h = await NewAsync();
        var claude = h.Actors.AddKey("Claude");
        var its = await h.FileAsync("story", "assigned to the agent", h.Todo, rank: 1024);
        await h.FileAsync("story", "nobody's", h.Todo, rank: 2048);
        await h.AssignAsync(its, apiKeyId: claude.Id);

        // "people only": assigning a ticket to Claude and having Claude stop
        // picking it up would read backwards.
        Assert.Equal(Key(its), Value(await h.Work.GetNextWork(0, null, default)).Issue.Key);
    }

    [Fact]
    public async Task NextWork_TakesAnIssueWhoseAssignedPersonHasBeenDeleted()
    {
        var h = await NewAsync();
        var hers = await h.FileAsync("story", "was Ada's", h.Todo, rank: 1024);
        await h.FileAsync("story", "nobody's", h.Todo, rank: 2048);

        // The column holds an id the directory does not know, which is what a
        // deleted person looks like from here. It is not assigned, so it is not
        // folded - the liveness rule reaching the dispatcher without a sweeper.
        await h.AssignAsync(hers, personId: Guid.NewGuid());

        Assert.Equal(Key(hers), Value(await h.Work.GetNextWork(0, null, default)).Issue.Key);
    }

    [Fact]
    public async Task Work_ForANamedKey_IsNotFoldedByAnAssignee()
    {
        var h = await NewAsync();
        var ada = h.Actors.AddPerson("Ada");
        var hers = await h.FileAsync("story", "Ada is on this", h.Todo, rank: 1024);
        await h.FileAsync("story", "nobody's", h.Todo, rank: 2048);
        await h.AssignAsync(hers, personId: ada.Id);

        // Named by hand, nothing refuses it: this is the loop's policy about
        // what it may *start*, not a fact about the work - the same line the
        // ready date sits on.
        Assert.Null(Value(await h.Work.GetWork(Key(hers), default)).Blocked);
    }

    [Fact]
    public async Task Queue_SaysAnIssueIsAssigned()
    {
        var h = await NewAsync();
        var ada = h.Actors.AddPerson("Ada");
        var hers = await h.FileAsync("story", "Ada is on this", h.Todo, rank: 1024);
        await h.AssignAsync(hers, personId: ada.Id);

        // The sentence, not just the fold: hatch.sh prints .blocked verbatim,
        // so this is the whole of what an operator reading a queue is told.
        var folded = Value(await h.Work.GetQueue(0, null, default)).Single(e => e.Issue.Key == Key(hers));
        Assert.Equal("assigned to Ada - an unattended pass leaves a person's work alone", folded.Blocked);
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
        Assert.Equal(Key(epic), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);
        Assert.IsType<NoContentResult>((await h.Work.GetNextWork(0, Key(epic), null, default)).Result);
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
    // Which types a move applies to is the playbook row's to say and nowhere
    // else's. What is left here is the loop's own policy - the ready date, and
    // nothing else since a dependency replaced the sibling rule. It is
    // `next`-only: a person who names a ticket is giving an instruction, and
    // housekeeping does not overrule it. A dependency is not housekeeping and
    // is asked of a named ticket too - see the dependency section below.

    [Fact]
    public async Task NextWork_TakesTheTopOfTheColumnWhateverTypeThePlaybookNames()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "somebody has to choose what this contains", h.Todo, rank: 1024);
        await h.FileAsync("story", "the unit that ships", h.Todo, rank: 2048);

        // A playbook names epics for todo to in progress, so an epic at the top
        // of the column is the top of the column. The matrix is the one
        // statement of which types a move applies to; a constant here saying it
        // a second time is what shadowed it.
        Assert.Equal(Key(epic), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);
    }

    [Fact]
    public async Task Work_OnANamedIssueIgnoresTheLoopsPolicy()
    {
        var h = await NewAsync();
        var held = await h.FileAsync("story", "not until the soak test", h.Todo, readyAt: Now.AddDays(3));
        await h.FileAsync("story", "workable now", h.Todo, rank: 2048);

        // A pass folds it, and the second story is why the board is not empty
        // and the fold has to be read off the scan rather than off a 204.
        var folded = Value(await h.Work.GetQueue(0, null, default)).Single(e => e.Issue.Key == Key(held));
        Assert.Contains("not workable until", folded.Blocked);

        // Named by hand, nothing refuses it: a ready date is a decision about
        // what an unattended run may *start*, not a fact about the issue.
        Assert.Null(Value(await h.Work.GetWork(Key(held), null, default)).Blocked);
    }

    [Fact]
    public async Task Work_OnANamedIssueAndOnAPass_AgreeAboutEveryType()
    {
        var h = await NewAsync();
        var filed = new Dictionary<string, string>();
        var rank = 1024L;
        foreach (var type in EfHatchIssue.Types)
            filed[type] = Key(await h.FileAsync(type, $"an ordinary {type}", h.Todo, rank: rank += 1024));

        var queue = Value(await h.Work.GetQueue(0, null, default)).ToDictionary(e => e.Issue.Key, e => e.Blocked);

        // Nothing about a type is the pass's to decide any more, so the two
        // verdicts cannot differ on one. Asserted per type, so a failure says
        // which type stopped agreeing rather than that something did.
        foreach (var (type, key) in filed)
            Assert.Equal((type, Value(await h.Work.GetWork(key, null, default)).Blocked), (type, queue[key]));
    }

    // ---- What it waits on ----
    //
    // A dependency gates one move: the one into the column an agent writes the
    // code in, which on this board is todo to in progress exactly as it is on a
    // stock one. Everything left of it still moves, an issue already in it
    // finishes, and satisfied means terminal - a blocker in review still
    // blocks, or the second story starts on the first one's unmerged branch.

    [Fact]
    public async Task NextWork_PassesOverAnIssueWaitingOnUnfinishedWork()
    {
        var h = await NewAsync();
        var first = await h.FileAsync("story", "phase one", h.Review, rank: 1024);
        var second = await h.FileAsync("story", "phase two", h.Todo, rank: 1024);
        await h.DependsAsync(second, first);

        var next = await h.FileAsync("story", "unrelated", h.Todo, rank: 2048);

        Assert.Equal(Key(next), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);
    }

    [Fact]
    public async Task ADependency_DoesNotHoldUpTheColumnsBeforeImplementation()
    {
        var h = await NewAsync();
        var first = await h.FileAsync("story", "phase one", h.Review);
        var second = await h.FileAsync("story", "phase two", h.Inbox);
        await h.DependsAsync(second, first);

        // The seeded matrix says nothing about inbox to todo, so this test
        // supplies the row - the fold under examination is the dependency's,
        // and a missing playbook would hide it.
        h.Db.Add(Playbook(h.Inbox, h.Todo, "", "sonnet"));
        await h.Db.SaveChangesAsync();

        // Still broken down, still landed in the backlog, still analysed. Only
        // the writing waits.
        Assert.Null(Value(await h.Work.GetWork(Key(second), null, default)).Blocked);
    }

    [Fact]
    public async Task ADependency_DoesNotStopWorkThatHasAlreadyStarted()
    {
        var h = await NewAsync();
        var first = await h.FileAsync("story", "phase one", h.Review);
        var second = await h.FileAsync("story", "phase two, half written", h.InProgress);
        await h.DependsAsync(second, first);

        // The move into review is not a dependency's to refuse: an issue that
        // is already being written finishes rather than stalling half-done.
        Assert.Null(Value(await h.Work.GetWork(Key(second), null, default)).Blocked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADependency_IsSatisfiedOnlyByATerminalColumn(bool merged)
    {
        var h = await NewAsync();
        var first = await h.FileAsync("story", "phase one", merged ? h.Done : h.Review);
        var second = await h.FileAsync("story", "phase two", h.Todo);
        await h.DependsAsync(second, first);

        var blocked = Value(await h.Work.GetWork(Key(second), null, default)).Blocked;

        if (merged) Assert.Null(blocked);
        else Assert.Contains($"{Key(first)} is not done", blocked);
    }

    [Fact]
    public async Task ADependencyAnAncestorHolds_ReachesEverythingBelowIt()
    {
        var h = await NewAsync();
        var first = await h.FileAsync("story", "phase one", h.Review);
        var second = await h.FileAsync("story", "phase two", h.Todo);
        await h.DependsAsync(second, first);

        var task = await h.FileAsync("task", "a piece of phase two", h.Todo, parentId: second.Id);

        // The task holds no edge of its own, and an epic-level "phase two after
        // phase one" would say nothing at all if it did not reach down.
        var blocked = Value(await h.Work.GetWork(Key(task), null, default)).Blocked;

        Assert.Contains($"{Key(first)} is not done", blocked);
        Assert.Contains($"{Key(second)} above this", blocked);
    }

    [Fact]
    public async Task NextWork_StartsASecondStoryUnderAParentThatIsAwaitingReview()
    {
        var h = await NewAsync();
        var parent = await h.FileAsync("epic", "the effort", h.Todo, rank: 8192);
        await h.FileAsync("story", "already up for review", h.Review, rank: 1024, parentId: parent.Id);
        var next = await h.FileAsync("story", "independent of it", h.Todo, rank: 1024, parentId: parent.Id);

        // What the sibling rule refused. Two stories under one epic with no
        // edge between them are two independent pieces of work, and the loop
        // says so by taking the second one.
        Assert.Equal(Key(next), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);
    }

    [Fact]
    public async Task Work_OnANamedIssueStillRefusesAnUnmetDependency()
    {
        var h = await NewAsync();
        var first = await h.FileAsync("story", "phase one", h.Review);
        var second = await h.FileAsync("story", "phase two", h.Todo);
        await h.DependsAsync(second, first);

        // Unlike the ready date, this is a fact about the work rather than
        // housekeeping - somebody who disagrees takes the edge off.
        Assert.Equal(
            $"{Key(first)} is not done, and this cannot be implemented until it is",
            Value(await h.Work.GetWork(Key(second), null, default)).Blocked);
    }

    [Fact]
    public async Task TheSentence_NamesEveryBlockerInKeyOrder()
    {
        var h = await NewAsync();
        var one = await h.FileAsync("story", "phase one", h.Review);
        var two = await h.FileAsync("story", "phase one and a half", h.Todo);
        var three = await h.FileAsync("story", "phase one and three quarters", h.Todo);
        var last = await h.FileAsync("story", "phase two", h.Todo);

        await h.DependsAsync(last, three);
        await h.DependsAsync(last, one);
        await h.DependsAsync(last, two);

        // Key order and not insertion order, so two passes over an unchanged
        // board print the same sentence.
        Assert.Equal(
            $"{Key(one)}, {Key(two)} and {Key(three)} are not done, "
            + "and this cannot be implemented until they are",
            Value(await h.Work.GetWork(Key(last), null, default)).Blocked);
    }

    // ---- Refusals ----

    [Fact]
    public async Task Work_RefusesToEnterATerminalColumn()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync("task", "ready for a verdict", h.Review);

        var work = Value(await h.Work.GetWork(Key(issue), null, default));

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

        var work = Value(await h.Work.GetWork(Key(issue), null, default));

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
        var work = Value(await h.Work.GetWork(Key(issue), null, default));

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

        var work = Value(await h.Work.GetWork(Key(issue), null, default));

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

        var work = Value(await h.Work.GetWork(Key(issue), null, default));

        Assert.Null(work.Blocked);
    }

    [Fact]
    public async Task Work_CarriesTheAnsweredQuestionsSoTheNextSessionDoesNotReopenThem()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync("task", "decided", h.Todo);
        var question = await h.AskAsync(issue, "per-node or global?");
        await h.AnswerAsync(issue, question, "per-node");

        var work = Value(await h.Work.GetWork(Key(issue), null, default));

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

        Assert.Equal("opus", Value(await h.Work.GetWork(Key(epic), null, default)).Playbook!.Model);
        Assert.Equal("sonnet", Value(await h.Work.GetWork(Key(task), null, default)).Playbook!.Model);
    }

    [Fact]
    public async Task Work_FallsBackToTheCatchAllForATypeNobodyNamed()
    {
        var h = await NewAsync();
        var bug = await h.FileAsync("bug", "unnamed by any specific row", h.Todo);

        var playbook = Value(await h.Work.GetWork(Key(bug), null, default)).Playbook;

        Assert.NotNull(playbook);
        Assert.Empty(playbook.Types);
    }

    // ---- The issue's own model and effort ----

    [Fact]
    public async Task Work_WithNoOverride_ReportsThePlaybooksOwnValues()
    {
        var h = await NewAsync();
        var task = await h.FileAsync("task", "an ordinary one", h.Todo);

        var playbook = Value(await h.Work.GetWork(Key(task), null, default)).Playbook!;

        Assert.Equal("sonnet", playbook.Model);
        Assert.Equal("high", playbook.Effort);
    }

    [Fact]
    public async Task Work_ReportsAModelOverrideInPlaceOfThePlaybooks()
    {
        var h = await NewAsync();
        var task = await h.FileAsync("task", "harder than its column suggests", h.Todo);
        await h.OverrideAsync(task, model: "opus");

        var playbook = Value(await h.Work.GetWork(Key(task), null, default)).Playbook!;

        // The value that won, in place - and the effort still the row's own,
        // because the two are independent.
        Assert.Equal("opus", playbook.Model);
        Assert.Equal("high", playbook.Effort);
    }

    [Fact]
    public async Task Work_ReportsAnEffortOverrideTheSameWay()
    {
        var h = await NewAsync();
        var task = await h.FileAsync("task", "subtle rather than large", h.Todo);
        await h.OverrideAsync(task, effort: "max");

        var playbook = Value(await h.Work.GetWork(Key(task), null, default)).Playbook!;

        Assert.Equal("sonnet", playbook.Model);
        Assert.Equal("max", playbook.Effort);
    }

    [Fact]
    public async Task Work_ReportsBothOverrides_AndTheRowThatSpoke()
    {
        var h = await NewAsync();
        var task = await h.FileAsync("task", "both", h.Todo);
        await h.OverrideAsync(task, model: "haiku", effort: "low");

        var work = Value(await h.Work.GetWork(Key(task), null, default));

        Assert.Equal("haiku", work.Playbook!.Model);
        Assert.Equal("low", work.Playbook.Effort);

        // The dispatch names the playbook that spoke as well as the values
        // that won: everything but the two fields is still the matched row's.
        Assert.Equal("do the thing", work.Playbook.Prompt);
        Assert.Equal(h.Todo, work.Playbook.FromStatusId);
        Assert.Equal(h.InProgress, work.Playbook.ToStatusId);

        // ...and the override rides the same payload, which is how a printed
        // line says where the value came from.
        Assert.Equal("haiku", work.Issue.ModelOverride);
        Assert.Equal("low", work.Issue.EffortOverride);
    }

    [Fact]
    public async Task Work_NextReportsTheOverrideToo()
    {
        var h = await NewAsync();
        var story = await h.FileAsync("story", "the only thing on the board", h.Todo);
        await h.OverrideAsync(story, model: "opus");

        // One fold point, both endpoints: `next` and `{key}` reach the same
        // resolve, so there is no second place to remember.
        Assert.Equal("opus", Value(await h.Work.GetNextWork(0, null, null, default)).Playbook!.Model);
    }

    [Fact]
    public async Task Work_AnOverrideOnAParent_DoesNotReachItsChild()
    {
        var h = await NewAsync();
        var story = await h.FileAsync("story", "the expensive one", h.Todo, rank: 4096);
        var task = await h.FileAsync("task", "under it", h.Todo, rank: 1024, parentId: story.Id);
        await h.OverrideAsync(story, model: "opus", effort: "max");

        var playbook = Value(await h.Work.GetWork(Key(task), null, default)).Playbook!;

        // This issue only. An epic set to opus does not spend opus on its
        // stories - a task that needs the big model says so itself.
        Assert.Equal("sonnet", playbook.Model);
        Assert.Equal("high", playbook.Effort);
    }

    [Fact]
    public async Task Work_AnOverrideChangesWhatADispatchCosts_NeverWhetherOneHappens()
    {
        var h = await NewAsync();
        var story = await h.FileAsync("story", "nowhere to go from here", h.Inbox);
        await h.OverrideAsync(story, model: "opus", effort: "max");

        var work = Value(await h.Work.GetWork(Key(story), null, default));

        // Nothing in the refusal ladder learns about overrides: an issue with
        // no playbook for its next move still dispatches nothing.
        Assert.Null(work.Playbook);
        Assert.Contains("no playbook covers", work.Blocked);
    }

    [Fact]
    public async Task Work_CarriesTheChildrenSoTheAgentCanDescend()
    {
        var h = await NewAsync();
        var story = await h.FileAsync("story", "parent", h.Todo);
        await h.FileAsync("task", "first", h.Todo, parentId: story.Id);
        await h.FileAsync("task", "second", h.Todo, parentId: story.Id);

        var work = Value(await h.Work.GetWork(Key(story), null, default));

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

        var queue = Value(await h.Work.GetQueue(0, null, default));

        // Rightmost column first, then top of the column down - the scheduling
        // policy, written out rather than implied by which card came back.
        Assert.Equal(
            new[] { judged, underway, top, waiting, filed }.Select(Key),
            queue.Select(e => e.Issue.Key));
    }

    [Fact]
    public async Task Queue_ListsEveryExpeditedCandidateBeforeEveryOtherOne()
    {
        var h = await NewAsync();
        var judged = await h.FileAsync("story", "awaiting the operator", h.Review);
        var underway = await h.FileAsync("story", "underway", h.InProgress);
        var hurry = await h.FileAsync("bug", "the one somebody is waiting on", h.Inbox);

        await h.ExpediteAsync(hurry);

        // An expedited bug in the leftmost column, above a non-expedited story
        // in the rightmost one. Right to left is the policy; this is the one
        // thing that comes before it.
        Assert.Equal(
            new[] { hurry, judged, underway }.Select(Key),
            Value(await h.Work.GetQueue(0, null, default)).Select(e => e.Issue.Key));
    }

    [Fact]
    public async Task Queue_KeepsTheBoardsOwnOrderInsideEachHalf()
    {
        var h = await NewAsync();
        var judged = await h.FileAsync("story", "awaiting the operator", h.Review);
        var top = await h.FileAsync("story", "top of todo", h.Todo, rank: 1024);
        var below = await h.FileAsync("story", "below it", h.Todo, rank: 2048);
        var hurryLeft = await h.FileAsync("story", "hurried, in the inbox", h.Inbox);
        var hurryRight = await h.FileAsync("story", "hurried, further along", h.Review, rank: 2048);

        await h.ExpediteAsync(hurryLeft);
        await h.ExpediteAsync(hurryRight);

        // Rightmost column first and (Rank, Id) within a column, applied twice:
        // once to the expedited candidates and once to everything else.
        Assert.Equal(
            new[] { hurryRight, hurryLeft, judged, top, below }.Select(Key),
            Value(await h.Work.GetQueue(0, null, default)).Select(e => e.Issue.Key));
    }

    [Fact]
    public async Task NextWork_TakesTheFirstUnblockedRowOfThatQueue()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "awaiting the operator", h.Review);
        var hurry = await h.FileAsync("story", "the hurried one", h.Todo);

        await h.ExpediteAsync(hurry);

        var queue = Value(await h.Work.GetQueue(0, null, default));
        var work = Value(await h.Work.GetNextWork(0, null, null, default));

        // Still one walk, reported and acted on. The queue expedite publishes
        // is the queue next takes.
        Assert.Equal(Key(hurry), work.Issue.Key);
        Assert.Equal(queue.First(e => e.Blocked is null).Issue.Key, work.Issue.Key);
    }

    [Fact]
    public async Task AnExpeditedIssueThatIsBlocked_IsFoldedWithTheSameSentenceAndThePassCarriesOn()
    {
        var h = await NewAsync();
        var hurried = await h.FileAsync("story", "hurried, waiting on a person", h.Todo, rank: 1024);
        var ordinary = await h.FileAsync("story", "waiting on a person", h.Todo, rank: 2048);
        var next = await h.FileAsync("story", "the one behind them", h.Todo, rank: 3072);
        await h.AskAsync(hurried, "per-node or global?");
        await h.AskAsync(ordinary, "per-node or global?");

        await h.ExpediteAsync(hurried);

        var queue = Value(await h.Work.GetQueue(0, null, default));

        // Expedite carries nothing past its own reason: the row is considered
        // first, folded with exactly the sentence the same fold gives an
        // ordinary issue, and the pass goes on to the next one.
        Assert.Equal(Key(hurried), queue[0].Issue.Key);
        Assert.NotNull(queue[0].Blocked);
        Assert.Equal(queue.Single(e => e.Issue.Key == Key(ordinary)).Blocked, queue[0].Blocked);
        Assert.Equal(Key(next), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);
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
        Assert.Equal([Key(live)], Value(await h.Work.GetQueue(0, null, default)).Select(e => e.Issue.Key));
    }

    /// <summary>
    /// Nothing comes off the shelf on a pass's say-so. The refusal names the
    /// column rather than saying "there is nowhere for this to go", which is
    /// true of a deferred column and no use to anybody reading a queue.
    /// </summary>
    [Fact]
    public async Task Queue_FoldsPastAShelvedIssueAndSaysWhy()
    {
        var h = await NewAsync();
        var parked = await h.FileAsync("story", "not now", h.Shelved);

        var entry = Value(await h.Work.GetQueue(0, null, default)).SingleOrDefault(e => e.Issue.Key == Key(parked));

        // Either it is not in the queue at all, or it is there refused - both
        // are "nothing is dispatched from here", and only the second has a
        // sentence to check.
        if (entry is not null) Assert.Contains("deferred", entry.Blocked);

        Assert.DoesNotContain(
            Key(parked),
            Value(await h.Work.GetQueue(0, null, default)).Where(e => e.Blocked is null).Select(e => e.Issue.Key));
    }

    /// <summary>
    /// Named by hand rather than found by a scan, which is the path that hands
    /// out an actual refusal to read - and the one somebody hits when they
    /// wonder why the loop is ignoring a ticket they parked last week.
    /// </summary>
    [Fact]
    public async Task Work_RefusesAShelvedIssueByName()
    {
        var h = await NewAsync();
        var parked = await h.FileAsync("story", "not now", h.Shelved);

        var work = Value(await h.Work.GetWork(Key(parked), null, default));

        Assert.Contains("deferred", work.Blocked);
    }

    /// <summary>
    /// The board closes over the gap. "In progress" advances into review even
    /// though a deferred column is sorted between them, which is the whole of
    /// why the geometry is measured off the drawn columns.
    /// </summary>
    [Fact]
    public async Task Queue_AdvancesPastADeferredColumnRatherThanIntoIt()
    {
        var h = await NewAsync();
        var live = await h.FileAsync("story", "being written", h.InProgress);

        var entry = Only(await h.Work.GetQueue(0, null, default));

        Assert.Equal(Key(live), entry.Issue.Key);
        Assert.Equal(h.Review, entry.ToStatus?.Id);
        Assert.Null(entry.Blocked);
    }

    [Fact]
    public async Task Queue_FirstClearEntryIsWhatNextReturns()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "awaiting the operator", h.Review);
        await h.FileAsync("epic", "not workable until the soak test ends", h.Todo, rank: 1024, readyAt: Now.AddDays(3));
        await h.FileAsync("story", "waiting on a renewal", h.Todo, rank: 2048, readyAt: Now.AddDays(3));
        await h.FileAsync("story", "the one it should take", h.Todo, rank: 3072);
        await h.FileAsync("story", "below it", h.Todo, rank: 4096);

        var queue = Value(await h.Work.GetQueue(0, null, default));
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
        var asked = await h.FileAsync("story", "waiting on a person", h.Todo);
        await h.AskAsync(asked, "per-node or global?");

        var queue = Value(await h.Work.GetQueue(0, null, default));

        Assert.All(queue, e => Assert.NotNull(e.Blocked));
        Assert.IsType<NoContentResult>((await h.Work.GetNextWork(0, null, null, default)).Result);
    }

    [Fact]
    public async Task Queue_OnAnEmptyBoardIsEmptyRatherThanRefused()
    {
        var h = await NewAsync();

        Assert.Empty(Value(await h.Work.GetQueue(0, null, default)));
    }

    // ---- ...and every fold it makes, named ----

    [Fact]
    public async Task Queue_NamesAReadyDateThatHasNotArrived()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "waiting on a renewal", h.Todo, readyAt: Now.AddDays(3));

        Assert.Contains("not workable until", Only(await h.Work.GetQueue(0, null, default)).Blocked);
    }

    [Fact]
    public async Task Queue_NamesAnUnansweredQuestion()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync("story", "waiting on a person", h.Todo);
        await h.AskAsync(issue, "how should retries be scoped?");

        Assert.Contains("unanswered question", Only(await h.Work.GetQueue(0, null, default)).Blocked);
    }

    [Fact]
    public async Task Queue_NamesTheUnfinishedWorkAnIssueWaitsOn()
    {
        var h = await NewAsync();
        var first = await h.FileAsync("story", "phase one", h.Review);
        var second = await h.FileAsync("story", "phase two", h.Todo);
        await h.DependsAsync(second, first);

        var blocked = Value(await h.Work.GetQueue(0, null, default))
            .Single(e => e.Issue.Key == Key(second)).Blocked;

        Assert.Equal(
            $"{Key(first)} is not done, and this cannot be implemented until it is",
            blocked);
    }

    [Fact]
    public async Task Queue_NamesTheAncestorHoldingTheEdge()
    {
        var h = await NewAsync();
        var first = await h.FileAsync("story", "phase one", h.Review);
        var second = await h.FileAsync("story", "phase two", h.Todo);
        await h.DependsAsync(second, first);
        var task = await h.FileAsync("task", "a piece of phase two", h.Todo, parentId: second.Id);

        var blocked = Value(await h.Work.GetQueue(0, null, default))
            .Single(e => e.Issue.Key == Key(task)).Blocked;

        // Whose edge it is matters to whoever reads the queue: the fix is on
        // the story, not on the task in front of them.
        Assert.Equal(
            $"{Key(first)} is not done, and {Key(second)} above this cannot be implemented until it is",
            blocked);
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
            Only(await h.Work.GetQueue(0, null, default)).Blocked);
    }

    [Fact]
    public async Task Queue_FoldsATypeOnlyWhereNoPlaybookCoversIt()
    {
        var h = await NewAsync();
        await h.FileAsync("epic", "somebody's paragraph", h.Inbox);

        var blocked = Only(await h.Work.GetQueue(0, null, default)).Blocked;

        // One statement of which types a move applies to, and it is the
        // playbook's. A type is a reason only where no row covers the
        // transition for it, and then the sentence names the fix rather than
        // an unwritten rule about what a run picks up.
        Assert.Equal(
            "no playbook covers \"inbox\" to \"todo\" for an epic - add one on the Playbooks page",
            blocked);
        Assert.DoesNotContain("unattended run", blocked);
    }

    [Fact]
    public async Task Queue_NamesTheNextColumnBeingTerminal()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "awaiting the operator", h.Review);

        Assert.Equal(
            "the next column is \"done\", and only the operator moves work there",
            Only(await h.Work.GetQueue(0, null, default)).Blocked);
    }

    [Fact]
    public async Task Queue_SaysTheBoardIsTheReasonBeforeItSaysThePlaybookIsMissing()
    {
        var h = await NewAsync();
        await h.FileAsync("epic", "an epic the operator must judge", h.Review);

        // Both are true. The one that no edit can change is the one worth
        // printing: writing a review-to-done playbook would not make this
        // issue an agent's to move.
        Assert.Contains("only the operator moves work there", Only(await h.Work.GetQueue(0, null, default)).Blocked);
    }

    [Fact]
    public async Task Queue_OrdersEachColumnExactlyAsTheBoardDoes()
    {
        var h = await NewAsync();

        // Interleaved ranks and out-of-order ids across two columns, so that
        // neither the insertion order nor the id can pass for the sort.
        await h.FileAsync("story", "todo, third", h.Todo, rank: 4096);
        await h.FileAsync("story", "in progress, second", h.InProgress, rank: 2048);
        await h.FileAsync("story", "todo, first", h.Todo, rank: 1024);
        await h.FileAsync("bug", "in progress, first", h.InProgress, rank: 1024);
        await h.FileAsync("task", "todo, second", h.Todo, rank: 2048);
        await h.FileAsync("epic", "in progress, third", h.InProgress, rank: 4096);

        var board = Value(await new BoardController(h.Db, h.Actors, TestClaims.With(), h.Time).GetBoard(default));
        var queue = Value(await h.Work.GetQueue(0, null, default));

        // Per column, not flat: the board is ordered by status id and the
        // queue is walked right to left, and what agrees is the sequence
        // inside a column. BoardController serves (StatusId, Rank, Id) and the
        // scan serves (Rank, Id) per column - a card's place in the queue is
        // its place on the board, and nothing between them re-sorts.
        foreach (var column in new[] { h.Todo, h.InProgress })
            Assert.Equal(
                board.Issues.Where(c => c.StatusId == column).Select(c => c.Key),
                queue.Where(e => e.FromStatus.Id == column).Select(e => e.Issue.Key));
    }

    // ---- The claim ----
    //
    // The sixth condition: an issue somebody is working right now is not one to
    // send a second agent at. The lease is written straight onto the row here -
    // the endpoints that take and release one are IssueClaimTests' business,
    // and these are about what the dispatcher does with the columns.

    [Fact]
    public async Task AnIssueUnderALiveClaim_IsFoldedPastWithTheSentence()
    {
        var h = await NewAsync();
        var held = await h.FileAsync("story", "somebody is on it", h.Todo, rank: 1024);
        var free = await h.FileAsync("story", "nobody is on it", h.Todo, rank: 2048);
        await h.ClaimAsync(held);

        var queue = Value(await h.Work.GetQueue(0, null, default));

        Assert.Equal(
            "hatch is working this from somewhere:/checkouts/one, last heard from just now",
            queue.Single(e => e.Issue.Key == Key(held)).Blocked);

        // And the pass takes the next thing rather than stopping.
        Assert.Equal(Key(free), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);
    }

    [Fact]
    public async Task AClaimFoldsBeforeAnythingElseCanBeSaidAboutTheIssue()
    {
        var h = await NewAsync();

        // A ticket with no playbook for its move *and* somebody three minutes
        // into it. "No playbook covers this" is a true sentence about the wrong
        // thing: the claim is the only fold that says work is happening now.
        var held = await h.FileAsync("story", "underway", h.Inbox);
        await h.ClaimAsync(held);

        Assert.Equal(
            "hatch is working this from somewhere:/checkouts/one, last heard from just now",
            Only(await h.Work.GetQueue(0, null, default)).Blocked);
    }

    [Fact]
    public async Task AClaimOlderThanTheTtl_FoldsNothing()
    {
        var h = await NewAsync();
        var abandoned = await h.FileAsync("story", "its runner died", h.Todo);
        await h.ClaimAsync(abandoned);

        // No job ran and nobody cleared anything. The clock moved, which is the
        // whole of the expiry rule - and this test fails the day somebody adds
        // a sweeper and makes the predicate decorative.
        h.Time.Advance(TimeSpan.FromSeconds(TestClaims.Ttl + 1));

        Assert.Null(Only(await h.Work.GetQueue(0, null, default)).Blocked);
        Assert.Equal(Key(abandoned), Value(await h.Work.GetNextWork(0, null, null, default)).Issue.Key);
    }

    [Fact]
    public async Task AnIssueOneAlreadyHolds_IsNotFoldedOnThatAccount()
    {
        var h = await NewAsync();
        var mine = await h.FileAsync("story", "mine", h.Todo);
        var token = await h.ClaimAsync(mine);

        // Re-reading the dispatch for a ticket one already holds is not a
        // conflict - it is what a runner does after taking the lease.
        Assert.Equal(Key(mine), Value(await h.Work.GetNextWork(0, null, token, default)).Issue.Key);
        Assert.Null(Value(await h.Work.GetWork(Key(mine), token, default)).Blocked);
    }

    [Fact]
    public async Task SomebodyElsesToken_DoesNotUnfoldTheirClaim()
    {
        var h = await NewAsync();
        var theirs = await h.FileAsync("story", "theirs", h.Todo);
        await h.ClaimAsync(theirs);

        Assert.NotNull(Only(await h.Work.GetQueue(0, null, default)).Blocked);
        Assert.NotNull(Value(await h.Work.GetWork(Key(theirs), Guid.NewGuid(), default)).Blocked);
    }

    [Fact]
    public async Task ANamedDispatch_IsRefusedByAClaimToo()
    {
        var h = await NewAsync();
        var held = await h.FileAsync("story", "underway", h.Todo);
        await h.ClaimAsync(held);

        // A claim is a fact about the issue, not the loop's own housekeeping:
        // a person naming a ticket somebody is mid-increment on gets the same
        // sentence, and it is the same method that wrote it.
        var work = Value(await h.Work.GetWork(Key(held), null, default));

        Assert.Equal(
            "hatch is working this from somewhere:/checkouts/one, last heard from just now",
            work.Blocked);
    }

    [Fact]
    public async Task ALiveClaim_RidesTheDispatchesIssueAndItsChildren()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the epic", h.Todo);
        var child = await h.FileAsync("story", "beneath it", h.Todo, parentId: epic.Id);
        await h.ClaimAsync(child, by: "somebody", runner: "elsewhere:/checkouts/two");

        var work = Value(await h.Work.GetWork(Key(epic), null, default));

        // The card a dispatch hands its session says who is on the child, and
        // says it without the token: that is a capability, not a fact.
        var drawn = Assert.Single(work.Children).Claim;
        Assert.NotNull(drawn);
        Assert.Equal("somebody", drawn.ClaimedBy);
        Assert.Equal("elsewhere:/checkouts/two", drawn.Runner);
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
        Assert.Equal([Key(story)], Value(await h.Work.GetQueue(0, Key(mine), default)).Select(e => e.Issue.Key));
    }

    [Fact]
    public async Task Queue_UnderAnEpic_ReordersOnlyWhatIsInsideTheScope()
    {
        var h = await NewAsync();
        var mine = await h.FileAsync("epic", "the one I am pushing", h.Todo);
        var judged = await h.FileAsync("story", "under mine, further along", h.Review, parentId: mine.Id);
        var hurry = await h.FileAsync("story", "under mine, hurried", h.Todo, parentId: mine.Id);
        var elsewhere = await h.FileAsync("story", "somebody else's, hurried", h.Review);

        await h.ExpediteAsync(hurry);
        await h.ExpediteAsync(elsewhere);

        // The scope narrows the candidates before either walk, so a hurried
        // ticket outside it is not reached at all - and inside it the float
        // applies exactly as it does to the whole board.
        Assert.Equal(
            new[] { hurry, judged }.Select(Key),
            Value(await h.Work.GetQueue(0, Key(mine), default)).Select(e => e.Issue.Key));
    }

    [Fact]
    public async Task Queue_UnderAKeyNobodyMinted_IsTheSentenceTheSearchEndpointUses()
    {
        var h = await NewAsync();

        var refusal = Assert.IsType<BadRequestObjectResult>((await h.Work.GetQueue(0, "AER-404", default)).Result);
        Assert.Equal("there is no AER-404", refusal.Value);
    }

    [Fact]
    public async Task Queue_CarriesTheTransitionAnIssueIsClearFor()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "ready to go", h.Todo);

        var entry = Only(await h.Work.GetQueue(0, null, default));

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
        public required FakeTimeProvider Time { get; init; }

        /// <summary>Who the house knows. Empty until a test says otherwise, which reads as "nobody is assigned to anything".</summary>
        public required StubActorDirectory Actors { get; init; }
        public required WorkController Work { get; init; }
        public required PlaybooksController Playbooks { get; init; }
        public required int ProjectId { get; init; }
        public required int Inbox { get; init; }
        public required int Todo { get; init; }
        public required int InProgress { get; init; }
        public required int Review { get; init; }
        public required int Done { get; init; }
        public required int Shelved { get; init; }

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
        /// An issue given to somebody, written straight to the columns - what
        /// the route that writes them accepts and refuses is
        /// <see cref="AssigneeControllerTests"/>'s business, and these tests are
        /// about what the dispatcher does with an assignee once one is set. An
        /// id the directory does not know is how a deleted person and a revoked
        /// key are written here, which is what they are.
        /// </summary>
        public async Task AssignAsync(EfHatchIssue issue, Guid? personId = null, Guid? apiKeyId = null)
        {
            issue.AssigneePersonId = personId;
            issue.AssigneeApiKeyId = apiKeyId;
            await Db.SaveChangesAsync();
        }

        /// <summary>
        /// An issue's own model and effort, set straight on the row - what the
        /// route that writes them refuses and permits is
        /// <see cref="IssuePlaybookControllerTests"/>'s business, and these
        /// tests are about what the dispatcher does with them once set.
        /// </summary>
        public async Task OverrideAsync(EfHatchIssue issue, string? model = null, string? effort = null)
        {
            issue.ModelOverride = model;
            issue.EffortOverride = effort;
            await Db.SaveChangesAsync();
        }

        /// <summary>
        /// <em>This one first</em>, written straight onto the row. What the
        /// route that writes it accepts and refuses - and above all who may
        /// press it - is <see cref="IssueExpediteControllerTests"/>'s business;
        /// these tests are about what the dispatcher does with the flag once it
        /// is set.
        /// </summary>
        public async Task ExpediteAsync(EfHatchIssue issue)
        {
            issue.Expedited = true;
            await Db.SaveChangesAsync();
        }

        /// <summary>
        /// A lease, written straight onto the row. What the claim endpoints
        /// accept and refuse is IssueClaimTests' business; these tests are
        /// about what the dispatcher does with a claim once it is there, which
        /// is why the columns are the input.
        /// </summary>
        public async Task<Guid> ClaimAsync(
            EfHatchIssue issue, string by = "hatch", string runner = "somewhere:/checkouts/one")
        {
            var token = Guid.NewGuid();
            var now = Time.GetUtcNow();

            issue.ClaimToken = token;
            issue.ClaimedBy = by;
            issue.ClaimRunner = runner;
            issue.ClaimedAt = now;
            issue.ClaimHeartbeatAt = now;
            await Db.SaveChangesAsync();

            return token;
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

        /// <summary>
        /// One issue made to wait on another, written straight to the table -
        /// these tests are about what an edge does to a dispatch, and what the
        /// route that writes one refuses is
        /// <see cref="IssueDependenciesControllerTests"/>'s business.
        /// </summary>
        public async Task DependsAsync(EfHatchIssue issue, EfHatchIssue blocker)
        {
            Db.Dependencies.Add(new EfHatchIssueDependency
            {
                IssueId = issue.Id,
                DependsOnId = blocker.Id,
                CreatedBy = "hatch-agent",
                CreatedAt = Now,
            });

            await Db.SaveChangesAsync();
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

        var project = new EfHatchProject { Key = "AER", Name = "Hatch", CreatedAt = Now };
        var inbox = new EfHatchStatus { Name = "inbox", SortOrder = 10 };
        var todo = new EfHatchStatus { Name = "todo", SortOrder = 20 };
        var doing = new EfHatchStatus { Name = "in progress", SortOrder = 30 };
        var review = new EfHatchStatus { Name = "review", SortOrder = 35 };
        var done = new EfHatchStatus { Name = "done", SortOrder = 40, IsTerminal = true };

        // Sorted into the middle of the board on purpose. Every landmark below
        // is measured off the columns the board draws, so a siding sitting
        // between two lanes has to change none of them - and if it ever does,
        // half of these tests say so at once.
        var shelved = new EfHatchStatus { Name = "shelved", SortOrder = 32, IsDeferred = true };
        db.AddRange(project, inbox, todo, doing, review, done, shelved);
        await db.SaveChangesAsync();

        db.AddRange(
            Playbook(todo.Id, doing.Id, "epic", "opus"),
            Playbook(todo.Id, doing.Id, "", "sonnet"),
            Playbook(doing.Id, review.Id, "", "sonnet"));
        await db.SaveChangesAsync();

        var actors = new StubActorDirectory();

        // One clock the tests can move, because the whole point of a lazily
        // expiring lease is that nothing has to run for it to end - advancing
        // this is the only way a claim dies.
        var time = new FakeTimeProvider(Now);

        return new Harness
        {
            Db = db,
            Time = time,
            Actors = actors,
            Work = new WorkController(db, actors, TestClaims.With(), time),
            Playbooks = new PlaybooksController(db, new FakeTimeProvider(Now)),
            ProjectId = project.Id,
            Inbox = inbox.Id,
            Todo = todo.Id,
            InProgress = doing.Id,
            Review = review.Id,
            Done = done.Id,
            Shelved = shelved.Id,
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

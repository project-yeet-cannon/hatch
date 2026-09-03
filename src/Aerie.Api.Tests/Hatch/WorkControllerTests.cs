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
        await h.FileAsync("task", "sitting in todo", h.Todo);
        var advanced = await h.FileAsync("task", "already underway", h.InProgress);

        var work = Value(await h.Work.GetNextWork(0, default));

        // Both are workable. The one nearer the end of the board wins, because
        // a board worked left to right starts everything and finishes nothing.
        Assert.Equal(Key(advanced), work.Issue.Key);
        Assert.Equal("review", work.ToStatus!.Name);
    }

    [Fact]
    public async Task NextWork_TakesTheTopOfTheColumn()
    {
        var h = await NewAsync();
        var first = await h.FileAsync("task", "top", h.Todo, rank: 1024);
        await h.FileAsync("task", "below it", h.Todo, rank: 2048);

        Assert.Equal(Key(first), Value(await h.Work.GetNextWork(0, default)).Issue.Key);
    }

    [Fact]
    public async Task NextWork_SkipsAnIssueWhoseReadyDateHasNotArrived()
    {
        var h = await NewAsync();
        await h.FileAsync("task", "waiting on a renewal", h.Todo, rank: 1024, readyAt: Now.AddDays(3));
        var workable = await h.FileAsync("task", "can start now", h.Todo, rank: 2048);

        // The folded card is above it and is passed over anyway - the board
        // hides it for the same reason (schedule.ts).
        Assert.Equal(Key(workable), Value(await h.Work.GetNextWork(0, default)).Issue.Key);
    }

    [Fact]
    public async Task NextWork_TakesAnIssueReadyLaterToday()
    {
        var h = await NewAsync();
        var today = await h.FileAsync("task", "ready at five", h.Todo, readyAt: Now.AddHours(5));

        // Ready from the start of the day it names, whatever hour was set: a
        // ticket that becomes workable at 5pm is not one nobody may look at
        // over breakfast.
        Assert.Equal(Key(today), Value(await h.Work.GetNextWork(0, default)).Issue.Key);
    }

    [Fact]
    public async Task NextWork_SaysNothingWhenTheOnlyWorkLeftIsTheOperatorsToJudge()
    {
        var h = await NewAsync();
        await h.FileAsync("task", "waiting on a human", h.Review);
        await h.FileAsync("task", "shipped", h.Done);

        // Review's only exit is terminal and done has no exit at all, so an
        // unattended run has nothing it may do - which is a 204, not a card.
        Assert.IsType<NoContentResult>((await h.Work.GetNextWork(0, default)).Result);
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
            Todo = todo.Id,
            InProgress = doing.Id,
            Review = review.Id,
            Done = done.Id,
        };
    }

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

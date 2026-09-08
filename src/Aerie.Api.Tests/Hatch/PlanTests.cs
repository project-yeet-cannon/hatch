using Aerie.Api.Modules.Hatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// The landscape read: every epic in the tracker and what it adds up to.
///
/// The arithmetic itself is pinned in <see cref="RollupTests"/>. What this file
/// is about is the shape of the answer - that the tree is drawn once, that an
/// epic's meter covers the whole subtree and not only the epics under it, and
/// that nothing filed outside an epic falls out of the page altogether.
/// </summary>
public class PlanTests
{
    // ---- The tree ----

    [Fact]
    public async Task ANestedEpic_IsDrawnUnderItsParentAndNotAgainAtTheTop()
    {
        var h = await NewAsync();
        var outer = await h.FileAsync("epic", "the programme", h.Todo);
        var inner = await h.FileAsync("epic", "one phase of it", h.Todo, parentId: outer.Id);

        var plan = Value(await h.Plan.GetPlan(null, default));

        var top = Assert.Single(plan.Epics);
        Assert.Equal(h.Key(outer), top.Issue.Key);

        var nested = Assert.Single(top.Children);
        Assert.Equal(h.Key(inner), nested.Issue.Key);

        // The nested epic knows where it hangs, so the page can link upward
        // without a second request.
        Assert.Equal(h.Key(outer), nested.Issue.ParentKey);
    }

    [Fact]
    public async Task AnEpicsRollup_CountsTheLeavesBelowANestedEpic()
    {
        var h = await NewAsync();
        var outer = await h.FileAsync("epic", "the programme", h.Todo);
        var inner = await h.FileAsync("epic", "one phase of it", h.Todo, parentId: outer.Id);
        var story = await h.FileAsync("story", "in the phase", h.Todo, parentId: inner.Id);

        await h.FileAsync("task", "a", h.Todo, parentId: story.Id);
        await h.FileAsync("task", "b", h.Done, parentId: story.Id);

        // And one story hanging straight off the outer epic, so the outer total
        // is not simply the inner one.
        var direct = await h.FileAsync("story", "not in the phase", h.Review, parentId: outer.Id);
        await h.FileAsync("task", "c", h.Review, parentId: direct.Id);

        var top = Assert.Single(Value(await h.Plan.GetPlan(null, default)).Epics);

        // Three leaves, two of them two levels below a nested epic. An epic
        // whose meter only counted the epics under it would read as one.
        Assert.Equal(3, top.Rollup.Leaves);
        Assert.Equal(1, top.Rollup.Done);
        Assert.False(top.IsLeaf);

        Assert.Equal(2, Assert.Single(top.Children).Rollup.Leaves);
    }

    [Fact]
    public async Task AnEpicWithNothingUnderIt_IsALeafInItsOwnColumn()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "nothing under it yet", h.Todo);

        var top = Assert.Single(Value(await h.Plan.GetPlan(null, default)).Epics);

        Assert.Equal(h.Key(epic), top.Issue.Key);
        Assert.True(top.IsLeaf);
        Assert.Empty(top.Children);
        Assert.Equal(1, top.Rollup.Leaves);
    }

    [Fact]
    public async Task TheEpics_ComeBackInKeyOrder()
    {
        // Numbered in the order they are filed, so filing them backwards and
        // ranking them backwards again leaves key order the one thing neither
        // the insertion order nor the board's order could pass for.
        var h = await NewAsync();
        var one = await h.FileAsync("epic", "filed first, ranked last", h.Todo, rank: 4096);
        var two = await h.FileAsync("epic", "filed second", h.Todo, rank: 1);
        var three = await h.FileAsync("epic", "filed last, ranked in the middle", h.Todo, rank: 2048);

        var plan = Value(await h.Plan.GetPlan(null, default));

        Assert.Equal(
            [h.Key(one), h.Key(two), h.Key(three)],
            plan.Epics.Select(e => e.Issue.Key));
    }

    // ---- What hangs under no epic ----

    [Fact]
    public async Task Loose_PicksUpAParentlessStorysTasksAndIgnoresEverythingUnderAnEpic()
    {
        var h = await NewAsync();

        var epic = await h.FileAsync("epic", "the epic", h.Todo);
        var inside = await h.FileAsync("story", "under the epic", h.Todo, parentId: epic.Id);
        await h.FileAsync("task", "a", h.Todo, parentId: inside.Id);
        await h.FileAsync("task", "b", h.Todo, parentId: inside.Id);

        var orphan = await h.FileAsync("story", "nobody parented it", h.Todo);
        await h.FileAsync("task", "c", h.Todo, parentId: orphan.Id);
        await h.FileAsync("task", "d", h.Done, parentId: orphan.Id);
        await h.FileAsync("bug", "on its own", h.Review);

        var plan = Value(await h.Plan.GetPlan(null, default));

        // The orphan story's two tasks and the childless bug. The epic's two
        // are drawn on the epic's own meter and must not be counted twice.
        Assert.Equal(3, plan.Loose.Leaves);
        Assert.Equal(1, plan.Loose.Done);
        Assert.Equal(
            new Dictionary<int, int> { [h.Todo] = 1, [h.Review] = 1, [h.Done] = 1 },
            Counts(plan.Loose));
    }

    [Fact]
    public async Task NothingOutsideAnEpic_IsAnEmptyLooseRatherThanAnAbsentOne()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "everything is filed", h.Todo);
        await h.FileAsync("story", "under it", h.Todo, parentId: epic.Id);

        var plan = Value(await h.Plan.GetPlan(null, default));

        Assert.Equal(0, plan.Loose.Leaves);
        Assert.Equal(0, plan.Loose.Done);
        Assert.Empty(plan.Loose.Slices);
    }

    [Fact]
    public async Task AnEmptyTracker_IsAnEmptyPlan()
    {
        var h = await NewAsync();

        var plan = Value(await h.Plan.GetPlan(null, default));

        Assert.Empty(plan.Epics);
        Assert.Equal(0, plan.Loose.Leaves);
    }

    // ---- The project filter ----

    [Fact]
    public async Task AProjectId_ScopesBothHalves()
    {
        var h = await NewAsync();

        var mine = await h.FileAsync("epic", "in AER", h.Todo);
        await h.FileAsync("story", "under it", h.Todo, parentId: mine.Id);
        await h.FileAsync("bug", "loose in AER", h.Todo);

        var theirs = await h.FileAsync("epic", "in OPS", h.Todo, project: h.Other);
        await h.FileAsync("story", "under that one", h.Todo, parentId: theirs.Id, project: h.Other);
        await h.FileAsync("bug", "loose in OPS", h.Todo, project: h.Other);
        await h.FileAsync("bug", "loose in OPS as well", h.Todo, project: h.Other);

        var here = Value(await h.Plan.GetPlan(h.ProjectId, default));
        Assert.Equal(h.Key(mine), Assert.Single(here.Epics).Issue.Key);
        Assert.Equal(1, here.Loose.Leaves);

        var there = Value(await h.Plan.GetPlan(h.Other, default));
        Assert.Equal(h.Key(theirs, "OPS"), Assert.Single(there.Epics).Issue.Key);
        Assert.Equal(2, there.Loose.Leaves);

        // And no filter is the house, both halves together.
        var all = Value(await h.Plan.GetPlan(null, default));
        Assert.Equal(2, all.Epics.Count);
        Assert.Equal(3, all.Loose.Leaves);
    }

    [Fact]
    public async Task AProjectIdNamingNothing_IsAnEmptyPlanRatherThanANotFound()
    {
        var h = await NewAsync();
        _ = await h.FileAsync("epic", "somewhere else", h.Todo);
        _ = await h.FileAsync("bug", "loose", h.Todo);

        var plan = Value(await h.Plan.GetPlan(404, default));

        // An empty project is a legal thing to look at, and so is one another
        // tab has just deleted.
        Assert.Empty(plan.Epics);
        Assert.Equal(0, plan.Loose.Leaves);
        Assert.Empty(plan.Loose.Slices);
    }

    // ---- Through a story, and around one ----

    [Fact]
    public async Task AnEpicBelowAStory_HangsOffTheEpicAboveThemBoth()
    {
        var h = await NewAsync();
        var outer = await h.FileAsync("epic", "the programme", h.Todo);
        var story = await h.FileAsync("story", "a story in it", h.Todo, parentId: outer.Id);
        var inner = await h.FileAsync("epic", "an epic under the story", h.Todo, parentId: story.Id);
        await h.FileAsync("task", "a", h.Todo, parentId: inner.Id);

        var top = Assert.Single(Value(await h.Plan.GetPlan(null, default)).Epics);

        // The nearest epic above it is what an epic hangs off, however many
        // stories sit in between - so every epic lands in exactly one list.
        var nested = Assert.Single(top.Children);
        Assert.Equal(h.Key(inner), nested.Issue.Key);

        // Its card still says where it actually hangs, which is the story.
        Assert.Equal(h.Key(story), nested.Issue.ParentKey);

        Assert.Equal(1, top.Rollup.Leaves);
    }

    [Fact]
    public async Task AnEpicUnderAParentlessStory_IsCountedInLooseAndNotDrawnTwice()
    {
        var h = await NewAsync();

        // The type rules refuse this pairing; a restored backup does not go
        // through the type rules. The requirement is that the work is counted
        // once and nowhere twice, not that the page finds somewhere to draw it.
        var root = await h.FileAsync("story", "a root that is not an epic", h.Todo);
        var epic = await h.FileAsync("epic", "somehow under it", h.Todo, parentId: root.Id);
        _ = await h.FileAsync("task", "a", h.Todo, parentId: epic.Id);

        var plan = Value(await h.Plan.GetPlan(null, default));

        Assert.Empty(plan.Epics);
        Assert.Equal(1, plan.Loose.Leaves);
    }

    [Fact]
    public async Task ACycleAmongEpics_Answers()
    {
        var h = await NewAsync();
        var first = await h.FileAsync("epic", "one", h.Todo);
        var second = await h.FileAsync("epic", "the other", h.Todo, parentId: first.Id);

        // Written straight to the table, as a restored backup would be.
        first.ParentId = second.Id;
        await h.Db.SaveChangesAsync();

        var plan = Value(await h.Plan.GetPlan(null, default));

        // Neither has a null parent any more, so neither is a root and the page
        // draws nothing. Wrong, and finished - which is the only pair available
        // once the data is impossible.
        Assert.Empty(plan.Epics);
    }

    // ---- The harness ----

    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private class Harness
    {
        public required HatchContext Db { get; init; }
        public required PlanController Plan { get; init; }
        public required int ProjectId { get; init; }
        public required int Other { get; init; }
        public required int Todo { get; init; }
        public required int Review { get; init; }
        public required int Done { get; init; }

        private int next = 1;

        public async Task<EfHatchIssue> FileAsync(
            string type, string title, int statusId,
            long rank = 1024, long? parentId = null, int? project = null)
        {
            var issue = new EfHatchIssue
            {
                ProjectId = project ?? ProjectId,
                Number = next++,
                Type = type,
                Title = title,
                Description = "",
                StatusId = statusId,
                Rank = rank,
                ParentId = parentId,
                CreatedBy = "operator",
                CreatedAt = Now,
                UpdatedAt = Now,
            };

            Db.Issues.Add(issue);
            await Db.SaveChangesAsync();
            return issue;
        }

        public string Key(EfHatchIssue issue, string projectKey = "AER") =>
            IssueKey.Format(projectKey, issue.Number);
    }

    /// <summary>Two projects, and a board with the shape the seed leaves behind.</summary>
    private static async Task<Harness> NewAsync()
    {
        var db = new HatchContext(
            new DbContextOptionsBuilder<HatchContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var project = new EfHatchProject { Key = "AER", Name = "Aerie", CreatedAt = Now };
        var other = new EfHatchProject { Key = "OPS", Name = "Operations", CreatedAt = Now };
        var todo = new EfHatchStatus { Name = "todo", SortOrder = 20 };
        var review = new EfHatchStatus { Name = "review", SortOrder = 35 };
        var done = new EfHatchStatus { Name = "done", SortOrder = 40, IsTerminal = true };
        db.AddRange(project, other, todo, review, done);
        await db.SaveChangesAsync();

        return new Harness
        {
            Db = db,
            Plan = new PlanController(db, new StubActorDirectory(), TestClaims.With(), new FakeTimeProvider(Now)),
            ProjectId = project.Id,
            Other = other.Id,
            Todo = todo.Id,
            Review = review.Id,
            Done = done.Id,
        };
    }

    private static Dictionary<int, int> Counts(RollupDto rollup) =>
        rollup.Slices.ToDictionary(s => s.StatusId, s => s.Count);

    private static T Value<T>(Microsoft.AspNetCore.Mvc.ActionResult<T> result) =>
        result.Value ?? throw new InvalidOperationException($"expected a value, got {result.Result?.GetType().Name ?? "nothing"}");
}

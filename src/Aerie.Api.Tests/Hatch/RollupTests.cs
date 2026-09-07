using Aerie.Api.Modules.Hatch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// The arithmetic every meter in Hatch is drawn from.
///
/// One invariant carries most of this file: a parent's rollup is exactly the
/// sum of its children's, which is what lets a stack of meters agree with the
/// one above it. It only holds because a childless issue rolls up as itself -
/// one leaf, in its own column - so that is pinned first and the sum is pinned
/// against it.
/// </summary>
public class RollupTests
{
    // ---- The unit ----

    [Fact]
    public async Task AChildlessIssue_RollsUpAsItself()
    {
        var h = await NewAsync();
        var task = await h.FileAsync("task", "on its own", h.Todo);

        var plan = Value(await h.Plan.GetIssuePlan(Key(task), default));

        // One leaf, in its own column. Not zero, and not "not applicable":
        // this is the base case the whole fold is built on.
        Assert.Equal(1, plan.Rollup.Leaves);
        Assert.Equal([new RollupSliceDto(h.Todo, 1)], plan.Rollup.Slices);
        Assert.Empty(plan.Children);
    }

    [Fact]
    public async Task AParent_IsExactlyTheSumOfItsChildren()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the epic", h.Todo);
        var first = await h.FileAsync("story", "twenty tasks worth", h.Todo, parentId: epic.Id);
        var second = await h.FileAsync("story", "one task worth", h.Todo, parentId: epic.Id);

        await h.FileAsync("task", "a", h.Todo, parentId: first.Id);
        await h.FileAsync("task", "b", h.Review, parentId: first.Id);
        await h.FileAsync("task", "c", h.Done, parentId: first.Id);
        await h.FileAsync("task", "d", h.Review, parentId: second.Id);

        var plan = Value(await h.Plan.GetIssuePlan(Key(epic), default));
        var children = plan.Children.ToDictionary(c => c.Issue.Key, c => c.Rollup);

        Assert.Equal(4, plan.Rollup.Leaves);
        Assert.Equal(
            Counts(plan.Rollup),
            Sum(children[Key(first)], children[Key(second)]));
    }

    [Fact]
    public async Task ThreeLevelsDeep_FoldsUpThroughTheMiddle()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the epic", h.Todo);
        var story = await h.FileAsync("story", "the story", h.Todo, parentId: epic.Id);
        var bug = await h.FileAsync("bug", "found in passing", h.Review, parentId: epic.Id);
        await h.FileAsync("task", "a", h.Todo, parentId: story.Id);
        await h.FileAsync("task", "b", h.InProgress, parentId: story.Id);

        var plan = Value(await h.Plan.GetIssuePlan(Key(epic), default));

        // Three leaves: the story's two tasks and the childless bug. Neither
        // the epic nor the story is one of them.
        Assert.Equal(3, plan.Rollup.Leaves);
        Assert.Equal(
            new Dictionary<int, int> { [h.Todo] = 1, [h.InProgress] = 1, [h.Review] = 1 },
            Counts(plan.Rollup));

        var middle = plan.Children.Single(c => c.Issue.Key == Key(story));
        Assert.False(middle.IsLeaf);
        Assert.Equal(2, middle.Rollup.Leaves);

        Assert.True(plan.Children.Single(c => c.Issue.Key == Key(bug)).IsLeaf);
    }

    [Fact]
    public async Task AParentsOwnColumn_StaysOutOfItsOwnMeter()
    {
        var h = await NewAsync();
        var story = await h.FileAsync("story", "sitting in review", h.Review);
        await h.FileAsync("task", "not started", h.Todo, parentId: story.Id);
        await h.FileAsync("task", "nor this one", h.Todo, parentId: story.Id);

        var plan = Value(await h.Plan.GetIssuePlan(Key(story), default));

        // The tasks are the work, so a story whose tasks are all in todo reads
        // as todo however far right the story itself has been dragged.
        Assert.Equal([new RollupSliceDto(h.Todo, 2)], plan.Rollup.Slices);
    }

    [Fact]
    public async Task ALeafInATerminalColumn_CountsTowardDone()
    {
        var h = await NewAsync();
        var story = await h.FileAsync("story", "half shipped", h.Todo);
        await h.FileAsync("task", "shipped", h.Done, parentId: story.Id);
        await h.FileAsync("task", "in review", h.Review, parentId: story.Id);
        await h.FileAsync("task", "not started", h.Todo, parentId: story.Id);

        var plan = Value(await h.Plan.GetIssuePlan(Key(story), default));

        Assert.Equal(3, plan.Rollup.Leaves);
        Assert.Equal(1, plan.Rollup.Done);
    }

    // ---- The slices ----

    [Fact]
    public async Task Slices_ComeBackInBoardOrderWithEmptyColumnsAbsent()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the epic", h.Todo);
        await h.FileAsync("task", "d", h.Done, parentId: epic.Id);
        await h.FileAsync("task", "a", h.Todo, parentId: epic.Id);
        await h.FileAsync("task", "b", h.Review, parentId: epic.Id);
        await h.FileAsync("task", "c", h.Todo, parentId: epic.Id);

        var plan = Value(await h.Plan.GetIssuePlan(Key(epic), default));

        // Left to right, and inbox and in-progress are absent rather than
        // present with a zero - the client holds the column list already and
        // does not need a row that draws nothing.
        Assert.Equal(
            [new RollupSliceDto(h.Todo, 2), new RollupSliceDto(h.Review, 1), new RollupSliceDto(h.Done, 1)],
            plan.Rollup.Slices);
    }

    [Fact]
    public async Task Children_AreTheDirectOnesInRankOrder()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the epic", h.Todo);
        var second = await h.FileAsync("story", "below", h.Todo, rank: 2048, parentId: epic.Id);
        var first = await h.FileAsync("story", "above", h.Todo, rank: 1024, parentId: epic.Id);
        await h.FileAsync("task", "a grandchild, not a child", h.Todo, parentId: first.Id);

        var plan = Value(await h.Plan.GetIssuePlan(Key(epic), default));

        Assert.Equal([Key(first), Key(second)], plan.Children.Select(c => c.Issue.Key));

        // A story with one task and a task with none must not look alike on the
        // wire, which is why isLeaf is a field and not "leaves == 1".
        Assert.False(plan.Children[0].IsLeaf);
        Assert.Equal(1, plan.Children[0].Rollup.Leaves);
        Assert.True(plan.Children[1].IsLeaf);
        Assert.Equal(1, plan.Children[1].Rollup.Leaves);
    }

    // ---- Waiting on a person ----

    [Fact]
    public async Task Waiting_CountsAnOpenQuestionTwoLevelsDown()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the epic", h.Todo);
        var story = await h.FileAsync("story", "the story", h.Todo, parentId: epic.Id);
        var task = await h.FileAsync("task", "the task", h.Todo, parentId: story.Id);
        await h.AskAsync(task, "how should retries be scoped?");

        var plan = Value(await h.Plan.GetIssuePlan(Key(epic), default));

        // What says an epic is blocked on a person rather than on an agent -
        // and the question is two levels below the epic, where nobody looking
        // at the top of the tree would see it.
        Assert.Equal(1, plan.Rollup.Waiting);
        Assert.Equal(1, plan.Children.Single().Rollup.Waiting);
    }

    [Fact]
    public async Task Waiting_CountsAQuestionOnTheIssueItself()
    {
        var h = await NewAsync();
        var story = await h.FileAsync("story", "the story", h.Todo);
        await h.FileAsync("task", "the task", h.Todo, parentId: story.Id);
        await h.AskAsync(story, "what should this be called?");

        // The one total that counts the issue and not only what is below it: a
        // question asked on the epic blocks the epic.
        Assert.Equal(1, Value(await h.Plan.GetIssuePlan(Key(story), default)).Rollup.Waiting);
    }

    [Fact]
    public async Task Waiting_DoesNotCountAnAnsweredQuestion()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the epic", h.Todo);
        var story = await h.FileAsync("story", "the story", h.Todo, parentId: epic.Id);
        var settled = await h.AskAsync(story, "per-node or global?");
        await h.AnswerAsync(story, settled, "per-node");
        await h.AskAsync(story, "and what is it called?");

        // One definition of "open", shared with the board and with the refusal
        // to dispatch: a question with an answer is a decision, not a blocker.
        Assert.Equal(1, Value(await h.Plan.GetIssuePlan(Key(epic), default)).Rollup.Waiting);
    }

    // ---- What the totals ignore ----

    [Fact]
    public async Task AReadyDateInTheFuture_ChangesNothing()
    {
        var h = await NewAsync();
        var story = await h.FileAsync("story", "the story", h.Todo);
        await h.FileAsync("task", "workable now", h.Todo, parentId: story.Id);
        await h.FileAsync("task", "waiting on a renewal", h.Todo, parentId: story.Id, readyAt: Now.AddDays(90));

        var plan = Value(await h.Plan.GetIssuePlan(Key(story), default));

        // A card folded off the board is still work. A total that shrank and
        // grew as dates arrived would not be a total.
        Assert.Equal(2, plan.Rollup.Leaves);
        Assert.Equal([new RollupSliceDto(h.Todo, 2)], plan.Rollup.Slices);
    }

    // ---- Impossible data ----

    [Fact]
    public async Task ACycleWrittenStraightIntoTheDatabase_Terminates()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the epic", h.Todo);
        var story = await h.FileAsync("story", "the story", h.Todo, parentId: epic.Id);

        // Parenting refuses to close a loop; a restored backup or a
        // hand-written UPDATE does not go through parenting. The requirement is
        // a wrong answer rather than a hung request, so what is asserted is
        // that there is an answer at all.
        epic.ParentId = story.Id;
        await h.Db.SaveChangesAsync();

        var plan = Value(await h.Plan.GetIssuePlan(Key(epic), default));

        Assert.Equal(0, plan.Rollup.Leaves);
        Assert.Empty(plan.Rollup.Slices);
    }

    [Fact]
    public async Task APlanForAKeyThatNamesNothing_IsNotFound()
    {
        var h = await NewAsync();

        Assert.IsType<NotFoundResult>((await h.Plan.GetIssuePlan("AER-404", default)).Result);
        Assert.IsType<NotFoundResult>((await h.Plan.GetIssuePlan("not-a-key", default)).Result);
    }

    // ---- The harness ----

    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private class Harness
    {
        public required HatchContext Db { get; init; }
        public required PlanController Plan { get; init; }
        public required int ProjectId { get; init; }
        public required int Todo { get; init; }
        public required int InProgress { get; init; }
        public required int Review { get; init; }
        public required int Done { get; init; }

        private int next = 1;

        public async Task<EfHatchIssue> FileAsync(
            string type, string title, int statusId,
            long rank = 1024, long? parentId = null, DateTimeOffset? readyAt = null)
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
        /// A question written straight to the table - these tests are about
        /// what an open question does to a total, and the endpoint that writes
        /// one is covered where the rest of the comment rules are.
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

    /// <summary>A board with the shape the seed leaves behind: five columns, the rightmost terminal.</summary>
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

        return new Harness
        {
            Db = db,
            Plan = new PlanController(db, TestClaims.With(), new FakeTimeProvider(Now)),
            ProjectId = project.Id,
            Todo = todo.Id,
            InProgress = doing.Id,
            Review = review.Id,
            Done = done.Id,
        };
    }

    private static string Key(EfHatchIssue issue) => IssueKey.Format("AER", issue.Number);

    /// <summary>The slices as a histogram, so two of them can be compared without minding their order.</summary>
    private static Dictionary<int, int> Counts(RollupDto rollup) =>
        rollup.Slices.ToDictionary(s => s.StatusId, s => s.Count);

    private static Dictionary<int, int> Sum(params RollupDto[] rollups)
    {
        var total = new Dictionary<int, int>();

        foreach (var slice in rollups.SelectMany(r => r.Slices))
            total[slice.StatusId] = total.GetValueOrDefault(slice.StatusId) + slice.Count;

        return total;
    }

    private static T Value<T>(ActionResult<T> result) =>
        result.Value ?? throw new InvalidOperationException($"expected a value, got {result.Result?.GetType().Name ?? "nothing"}");
}

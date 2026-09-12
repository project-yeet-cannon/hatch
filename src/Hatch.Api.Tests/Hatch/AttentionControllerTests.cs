using Hatch.Api.Common;
using Hatch.Api.Ef;
using Hatch.Api.Modules.Hatch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Tests.Hatch;

/// <summary>
/// What the nav strip is told is waiting on a person.
///
/// The rules pinned here are the ones a widget cannot recover from getting
/// wrong. A control that lights up for a ticket nobody can act on is one nobody
/// reads after a week, so an issue in review with no pull request is counted and
/// never listed. And the review column is measured off the board's shape rather
/// than off its name, so renaming or reordering columns has to change nothing -
/// which is the whole reason <see cref="Columns"/> exists as a shared static.
/// </summary>
public class AttentionControllerTests
{
    // ---- The pull request half ----

    [Fact]
    public async Task Attention_ListsTheReviewColumnsIssuesThatHaveAPullRequest()
    {
        var h = await NewAsync();
        var up = await h.FileAsync("story", "delivered", h.Review, pullRequestUrl: "https://forge.example/pulls/1");
        await h.FileAsync("story", "still being written", h.InProgress, pullRequestUrl: "https://forge.example/pulls/2");

        var row = Assert.Single(Value(await h.Attention.GetAttention(default)).Reviews);

        Assert.Equal(Key(up), row.Key);
        Assert.Equal("delivered", row.Title);
        Assert.Equal("story", row.Type);
        Assert.Equal("https://forge.example/pulls/1", row.PullRequestUrl);
    }

    [Fact]
    public async Task Attention_KeepsTheColumnsOwnOrder()
    {
        var h = await NewAsync();
        var second = await h.FileAsync("story", "below", h.Review, rank: 2048, pullRequestUrl: "https://forge.example/pulls/2");
        var first = await h.FileAsync("story", "top", h.Review, rank: 1024, pullRequestUrl: "https://forge.example/pulls/1");

        // The board's own (Rank, Id), so a row sits where the eye already found
        // it - not in the order the issues were filed.
        Assert.Equal(
            [Key(first), Key(second)],
            Value(await h.Attention.GetAttention(default)).Reviews.Select(r => r.Key));
    }

    [Fact]
    public async Task Attention_CountsAnIssueInReviewWithNoPullRequestRatherThanListingIt()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "a phase story nobody will open a branch for", h.Review);
        await h.FileAsync("story", "another", h.Review);
        await h.FileAsync("story", "delivered", h.Review, pullRequestUrl: "https://forge.example/pulls/1");

        var attention = Value(await h.Attention.GetAttention(default));

        // Never a row, so it never makes the control loud - and never silently
        // dropped either, so a ticket whose agent forgot `hatch pr` is visible.
        Assert.Single(attention.Reviews);
        Assert.Equal(2, attention.InReviewWithoutPullRequest);
    }

    [Fact]
    public async Task Attention_TreatsABlankPullRequestUrlAsNoneAtAll()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "cleared, badly", h.Review, pullRequestUrl: "   ");

        var attention = Value(await h.Attention.GetAttention(default));

        Assert.Empty(attention.Reviews);
        Assert.Equal(1, attention.InReviewWithoutPullRequest);
    }

    [Fact]
    public async Task Attention_SaysNothingIsInReviewWhenNothingIs()
    {
        var h = await NewAsync();
        await h.FileAsync("story", "underway", h.InProgress, pullRequestUrl: "https://forge.example/pulls/1");

        var attention = Value(await h.Attention.GetAttention(default));

        // The two emptinesses the panel tells apart: none at all, versus some
        // with no link. This is the first, and the count is what says so.
        Assert.Empty(attention.Reviews);
        Assert.Equal(0, attention.InReviewWithoutPullRequest);
    }

    // ---- Which column review is ----

    [Fact]
    public async Task Attention_FindsTheReviewColumnAfterItIsRenamed()
    {
        var h = await NewAsync();
        await h.RenameAsync(h.Review, "Waiting on Nathan");
        var up = await h.FileAsync("story", "delivered", h.Review, pullRequestUrl: "https://forge.example/pulls/1");

        // Measured, not named: the column immediately left of the first
        // terminal one, whatever anybody has called it.
        Assert.Equal(Key(up), Assert.Single(Value(await h.Attention.GetAttention(default)).Reviews).Key);
    }

    [Fact]
    public async Task Attention_FollowsTheReviewColumnWhenTheBoardIsReordered()
    {
        var h = await NewAsync();
        var moved = await h.FileAsync("story", "in the column that is now last before done", h.InProgress, pullRequestUrl: "https://forge.example/pulls/1");
        await h.FileAsync("story", "in the one that used to be", h.Review, pullRequestUrl: "https://forge.example/pulls/2");

        // The two columns trade places, so "in progress" is now the one
        // immediately left of terminal - and it is the one that answers.
        await h.ReorderAsync(h.InProgress, 36);

        Assert.Equal(Key(moved), Assert.Single(Value(await h.Attention.GetAttention(default)).Reviews).Key);
    }

    /// <summary>
    /// The landmark is measured off the columns the board draws, so a siding
    /// parked between review and done does not become "the column before
    /// terminal" - which would have the panel hunting for pull requests in a
    /// column nothing is ever dispatched from.
    /// </summary>
    [Fact]
    public async Task Attention_IgnoresADeferredColumnWhenItFindsTheReviewColumn()
    {
        var h = await NewAsync();
        var up = await h.FileAsync("story", "delivered", h.Review, pullRequestUrl: "https://forge.example/pulls/1");
        await h.ShelfAsync(37);

        Assert.Equal(Key(up), Assert.Single(Value(await h.Attention.GetAttention(default)).Reviews).Key);
    }

    /// <summary>
    /// And a shelved ticket is not in review however the board is shaped: the
    /// column it sits in is not the review column, so it is neither listed nor
    /// counted as one waiting for a link.
    /// </summary>
    [Fact]
    public async Task Attention_SaysNothingAboutAShelvedIssue()
    {
        var h = await NewAsync();
        var shelf = await h.ShelfAsync(37);
        await h.FileAsync("story", "parked with a branch open", shelf, pullRequestUrl: "https://forge.example/pulls/1");

        var attention = Value(await h.Attention.GetAttention(default));

        Assert.Empty(attention.Reviews);
        Assert.Equal(0, attention.InReviewWithoutPullRequest);
    }

    [Fact]
    public async Task Attention_AnswersEmptyOnABoardWithNoRoomForAReviewColumn()
    {
        var h = await OneColumnAsync();

        var attention = Value(await h.Attention.GetAttention(default));

        // A board whose only column is terminal has no column left of it. The
        // section draws its empty state; nothing here throws.
        Assert.Empty(attention.Reviews);
        Assert.Equal(0, attention.InReviewWithoutPullRequest);
    }

    // ---- The question half ----

    [Fact]
    public async Task Attention_ListsEveryOpenQuestionInTheHouseOldestFirst()
    {
        var h = await NewAsync();
        var one = await h.FileAsync("story", "asking about scope", h.Todo);
        var two = await h.FileAsync("story", "asking about a name", h.Inbox);
        await h.AskAsync(two, "what should it be called?", at: Now.AddHours(2));
        await h.AskAsync(one, "per-node or global?", at: Now.AddHours(1));

        var questions = Value(await h.Attention.GetAttention(default)).Questions;

        // Answering order, wherever the issue happens to stand: the question
        // that has waited longest is the one holding something up longest.
        Assert.Equal(["per-node or global?", "what should it be called?"], questions.Select(q => q.Body));
        Assert.Equal(Key(one), questions[0].IssueKey);
        Assert.Equal("asking about scope", questions[0].IssueTitle);
    }

    [Fact]
    public async Task Attention_LeavesOutAQuestionSomebodyHasAnswered()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync("story", "settled", h.Todo);
        var question = await h.AskAsync(issue, "per-node or global?");
        await h.AnswerAsync(issue, question, "per-node");

        // Open is computed, not stored - the same call the board badges a card
        // with, so this list and that badge cannot disagree.
        Assert.Empty(Value(await h.Attention.GetAttention(default)).Questions);
    }

    // ---- The gate ----

    [Fact]
    public void ReadingAttention_IsOpenToAHatchScopedKey()
    {
        var guard = typeof(AttentionController)
            .GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false)
            .Cast<RequireAdminAttribute>()
            .Single();

        // The same gate /board and /questions carry: it is the same house data,
        // read from the nav strip instead of from a page.
        Assert.Equal(ApiKeyScopes.Hatch, guard.AcceptScope);
    }

    // ---- Harness ----

    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public required HatchContext Db { get; init; }
        public required AttentionController Attention { get; init; }
        public required int ProjectId { get; init; }
        public required int Inbox { get; init; }
        public required int Todo { get; init; }
        public required int InProgress { get; init; }
        public required int Review { get; init; }

        private int next = 1;

        /// <summary>
        /// An issue placed directly. These tests are about which column an
        /// issue stands in and what it carries, so the column, the rank and the
        /// URL are the inputs; what the routes that write them refuse is
        /// <see cref="IssuesControllerTests"/>' business.
        /// </summary>
        public async Task<EfHatchIssue> FileAsync(
            string type, string title, int statusId, long rank = 1024, string? pullRequestUrl = null)
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
                PullRequestUrl = pullRequestUrl,
                CreatedBy = "operator",
                CreatedAt = Now,
                UpdatedAt = Now,
            };

            Db.Issues.Add(issue);
            await Db.SaveChangesAsync();
            return issue;
        }

        /// <summary>What an operator does on the Statuses page, written straight to the row.</summary>
        public async Task RenameAsync(int statusId, string name)
        {
            (await Db.Statuses.SingleAsync(s => s.Id == statusId)).Name = name;
            await Db.SaveChangesAsync();
        }

        /// <summary>The other thing they do there: dragging a column somewhere else.</summary>
        public async Task ReorderAsync(int statusId, int sortOrder)
        {
            (await Db.Statuses.SingleAsync(s => s.Id == statusId)).SortOrder = sortOrder;
            await Db.SaveChangesAsync();
        }

        /// <summary>A deferred column dropped into the board at a given position.</summary>
        public async Task<int> ShelfAsync(int sortOrder)
        {
            var shelf = new EfHatchStatus { Name = "shelved", SortOrder = sortOrder, IsDeferred = true };
            Db.Statuses.Add(shelf);
            await Db.SaveChangesAsync();
            return shelf.Id;
        }

        public async Task<EfHatchComment> AskAsync(EfHatchIssue issue, string body, DateTimeOffset? at = null)
        {
            var comment = new EfHatchComment
            {
                IssueId = issue.Id,
                Author = "hatch-agent",
                Body = body,
                Kind = EfHatchComment.Question,
                CreatedAt = at ?? Now,
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

    /// <summary>A board with the shape the Playbooks migration leaves behind.</summary>
    private static async Task<Harness> NewAsync()
    {
        var db = NewDb();

        var project = new EfHatchProject { Key = "AER", Name = "Hatch", CreatedAt = Now };
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
            Attention = new AttentionController(db),
            ProjectId = project.Id,
            Inbox = inbox.Id,
            Todo = todo.Id,
            InProgress = doing.Id,
            Review = review.Id,
        };
    }

    /// <summary>
    /// A board too short to have a review column at all: one terminal column,
    /// with nothing to its left. Not a board anybody would keep, but it is one
    /// the Statuses page can be left in halfway through building a new one.
    /// </summary>
    private static async Task<Harness> OneColumnAsync()
    {
        var db = NewDb();

        var project = new EfHatchProject { Key = "AER", Name = "Hatch", CreatedAt = Now };
        var done = new EfHatchStatus { Name = "done", SortOrder = 10, IsTerminal = true };
        db.AddRange(project, done);
        await db.SaveChangesAsync();

        return new Harness
        {
            Db = db,
            Attention = new AttentionController(db),
            ProjectId = project.Id,
            Inbox = done.Id,
            Todo = done.Id,
            InProgress = done.Id,
            Review = done.Id,
        };
    }

    private static HatchContext NewDb() => new(
        new DbContextOptionsBuilder<HatchContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>The display key of an issue these tests filed directly.</summary>
    private static string Key(EfHatchIssue issue) => IssueKey.Format("AER", issue.Number);

    private static T Value<T>(ActionResult<T> result) =>
        result.Value ?? throw new InvalidOperationException($"expected a value, got {result.Result?.GetType().Name ?? "nothing"}");
}

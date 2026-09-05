using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// One definition of "open", used by everything that asks.
///
/// A question is open when no comment answers it - computed here rather than
/// stored as a flag on the row, so a question cannot be open and answered at
/// the same time because two writes disagreed. Three callers ask the same thing
/// for different reasons: <see cref="BoardController"/> to badge a card,
/// <see cref="WorkController"/> to refuse a dispatch, and
/// <see cref="QuestionsController"/> to hand somebody the list to answer. They
/// share this file so that they cannot come to three different answers.
/// </summary>
public static class Questions
{
    /// <summary>Every unanswered question in the house.</summary>
    public static IQueryable<EfHatchComment> Open(HatchContext db) =>
        db.Comments.AsNoTracking()
            .Where(c => c.Kind == EfHatchComment.Question)
            .Where(c => !db.Comments.Any(a => a.AnswersId == c.Id));

    /// <summary>How many questions each issue is waiting on. Issues waiting on none are absent rather than zero.</summary>
    public static async Task<Dictionary<long, int>> OpenCountsAsync(HatchContext db, CancellationToken ct) =>
        await Open(db)
            .GroupBy(c => c.IssueId)
            .Select(g => new { IssueId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.IssueId, x => x.Count, ct);

    /// <summary>
    /// The stored options as a list, or null for a question asked in prose.
    /// </summary>
    /// <remarks>
    /// A row that will not parse reads as absent rather than throwing the
    /// question away - the same rule the event trail follows for its payload.
    /// The body is the question; the options are how it is offered, and a
    /// question that loses its menu is still answerable in the box below it.
    /// </remarks>
    public static IReadOnlyList<QuestionOptionDto>? ReadOptions(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;

        try
        {
            var options = JsonSerializer.Deserialize<List<QuestionOptionDto>>(stored, Json);
            return options is { Count: > 0 } ? options : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What goes in the column, or null when nothing was offered.</summary>
    public static string? WriteOptions(IReadOnlyList<QuestionOptionDto>? options) =>
        options is { Count: > 0 } ? JsonSerializer.Serialize(options, Json) : null;

    /// <summary>
    /// camelCase, matching the wire. The column is read by the browser through
    /// the same DTO it is written from, and a jsonb column whose casing
    /// disagreed with its own API would be a trap laid for whoever queries it
    /// in SQL one day.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Every question ever asked about one issue, answers attached, oldest first.</summary>
    public static Task<List<QuestionDto>> ForIssueAsync(HatchContext db, long issueId, CancellationToken ct) =>
        ProjectAsync(db, db.Comments.AsNoTracking()
            .Where(c => c.IssueId == issueId && c.Kind == EfHatchComment.Question), ct);

    /// <summary>
    /// Questions with their answers attached, in two queries rather than one
    /// per question: the house-wide list is read when the board is stuck, which
    /// is exactly when there are most of them.
    /// </summary>
    /// <remarks>
    /// Oldest first, everywhere, because that is answering order - the question
    /// that has been waiting longest is the one holding something up longest.
    /// </remarks>
    public static async Task<List<QuestionDto>> ProjectAsync(HatchContext db, IQueryable<EfHatchComment> questions, CancellationToken ct)
    {
        var rows = await questions
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Select(c => new
            {
                c.Id,
                c.Body,
                c.Author,
                c.Options,
                c.CreatedAt,
                IssueProjectKey = c.Issue!.Project!.Key,
                IssueNumber = c.Issue!.Number,
                IssueTitle = c.Issue!.Title,
            })
            .ToListAsync(ct);

        if (rows.Count == 0) return [];

        var ids = rows.Select(r => r.Id).ToList();
        var answers = await db.Comments.AsNoTracking()
            .Where(c => c.AnswersId != null && ids.Contains(c.AnswersId.Value))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.Author, c.Body, c.Kind, c.AnswersId, c.Options, c.CreatedAt })
            .ToListAsync(ct);

        var byQuestion = answers
            .Select(a => new CommentDto(a.Id, a.Author, a.Body, a.Kind, a.AnswersId, ReadOptions(a.Options), a.CreatedAt))
            .GroupBy(a => a.AnswersId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CommentDto>)g.ToList());

        return rows.Select(r => new QuestionDto(
            r.Id,
            IssueKey.Format(r.IssueProjectKey, r.IssueNumber),
            r.IssueTitle,
            r.Body,
            r.Author,
            r.CreatedAt,
            ReadOptions(r.Options),
            byQuestion.TryGetValue(r.Id, out var found) ? found : [])).ToList();
    }
}

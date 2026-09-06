using System.Text;
using System.Text.Json;
using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// The way <c>docs/plans/*.md</c> gets into Hatch: upload the files, look at
/// what they would become, then write it.
///
/// Two endpoints rather than one because an import is not undoable in the MVP -
/// there is no "undo import" button and forty issues filed by mistake are forty
/// deletes. Preview is the confirmation step, and it touches nothing.
/// </summary>
/// <remarks>
/// The importer only ever copies. Retiring the source <c>.md</c> file stays a
/// deliberate manual act (docs/hatch.md, "The importer") - this
/// endpoint has no idea where the file it was handed lives, which is the point.
/// </remarks>
[ApiController]
[Route("api/hatch/import")]
[RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
public class ImportController(HatchContext db, PlanImportParser parser, RankService ranks, ICallerIdentity caller, TimeProvider time) : ControllerBase
{
    /// <summary>A plan file is prose; a megabyte of it is a mistake, not a plan.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>
    /// How many files one upload may carry. A bound rather than a considered
    /// number: the whole <c>docs/plans</c> directory is a couple of dozen files,
    /// and the plan's own lifecycle asks for them one at a time anyway.
    /// </summary>
    public const int MaxFiles = 25;

    private const int MintAttempts = 5;

    // ---- Looking ----

    /// <summary>
    /// What these files would become. Reads nothing and writes nothing - the
    /// parse is pure, so this is the same tree <see cref="Import"/> will write
    /// if the operator says yes.
    /// </summary>
    /// <remarks>
    /// The size bound is <see cref="RequestSizeLimitAttribute"/> rather than a
    /// check after the read, because Kestrel enforces it <em>during</em> the
    /// read: an over-large upload aborts partway through instead of being
    /// buffered whole and then measured. The per-file check below is the
    /// finer-grained refusal underneath it, and says which file.
    /// </remarks>
    [HttpPost("preview")]
    [RequestSizeLimit((long)MaxFileBytes * MaxFiles + 64 * 1024)]
    public async Task<ActionResult<IReadOnlyList<ParsedEpic>>> Preview([FromForm] IFormFileCollection files, CancellationToken ct)
    {
        if (files is null || files.Count == 0) return BadRequest("no files were uploaded");
        if (files.Count > MaxFiles) return BadRequest($"that is {files.Count} files - {MaxFiles} at a time is the limit");

        var parsed = new List<ParsedEpic>(files.Count);

        foreach (var file in files)
        {
            var name = Path.GetFileName(file.FileName.Replace('\\', '/'));

            if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                return BadRequest($"\"{name}\" is not a .md file - the importer reads plans, not attachments");

            if (file.Length > MaxFileBytes)
                return BadRequest($"\"{name}\" is larger than {MaxFileBytes / 1024} KB");

            using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
            parsed.Add(parser.Parse(name, await reader.ReadToEndAsync(ct)));
        }

        return parsed;
    }

    /// <summary>
    /// The same look, for a plan that was pasted rather than uploaded: one
    /// title, one body, the tree they would become.
    ///
    /// It exists because a plan does not have to be a file to be worth filing.
    /// A page of notes in a chat window, a phase list somebody typed out - the
    /// board wants those on it, and saving them to <c>docs/plans/scratch.md</c>
    /// first only to upload them is a step that buys nothing.
    /// </summary>
    /// <remarks>
    /// JSON rather than a second multipart route, and the same
    /// <see cref="PlanImportParser"/> rather than a second reading of markdown.
    /// The parser already treats the source name as the epic's fallback title
    /// (<c>Title(h1, source)</c>), so a pasted title behaves exactly as a
    /// filename does: the body's own <c>#</c> heading wins when there is one,
    /// and the title stands in when there is not.
    ///
    /// One <see cref="ParsedEpic"/>, not a list: the operator is looking at one
    /// document. The page wraps it in the same array the upload path produces,
    /// so the preview, the count on the button, and <see cref="Import"/> itself
    /// are all reached unchanged - what gets written is written by exactly one
    /// code path, whichever way the text arrived.
    /// </remarks>
    [HttpPost("preview-text")]
    public ActionResult<ParsedEpic> PreviewText(PastedPlan plan)
    {
        var title = plan.Title?.Trim() ?? string.Empty;

        if (title.Length == 0)
            return BadRequest("a pasted plan needs a title - it is the name every issue from it will carry");

        // Bounded for the same reason an issue's title is, and not only for
        // tidiness: the provenance footer the parser appends is built from this
        // name, and a name longer than a description column would leave no room
        // for the description it is a footer to.
        if (title.Length > EfHatchIssue.MaxTitleLength)
            return BadRequest($"that title is {title.Length} characters - {EfHatchIssue.MaxTitleLength} is the limit");

        if (plan.Body is null || plan.Body.Trim().Length == 0)
            return BadRequest("there is nothing to read - the body is where the plan goes");

        // The same cap the upload path puts on one file, measured the same way,
        // so pasting a plan and uploading it are refused at the same size
        // rather than at two that happen to differ.
        if (Encoding.UTF8.GetByteCount(plan.Body) > MaxFileBytes)
            return BadRequest($"that is larger than {MaxFileBytes / 1024} KB");

        return parser.Parse(title, plan.Body);
    }

    // ---- Writing ----

    /// <summary>
    /// Files the tree the operator approved: an epic per document, a story per
    /// phase, a task per checkbox, each parented to the one above it and landed
    /// in the column its checked state asks for.
    /// </summary>
    /// <remarks>
    /// The whole import is one <c>SaveChanges</c>, retried as a whole. That is
    /// the same optimistic mint Phase 1's create path uses - the project's
    /// <c>NextIssueNumber</c> is a concurrency token, so a number taken
    /// underneath us throws rather than duplicating a key - applied to a batch,
    /// which additionally means an import either lands or does not. Half a plan
    /// on the board is worse than no plan on the board, because the half is not
    /// signposted.
    /// </remarks>
    [HttpPost]
    public async Task<ActionResult<ImportResultDto>> Import(ImportRequest request, CancellationToken ct)
    {
        if (request.Docs is not { Count: > 0 } docs) return BadRequest("there is nothing to import");
        if (docs.Count > MaxFiles) return BadRequest($"that is {docs.Count} documents - {MaxFiles} at a time is the limit");

        var statuses = await db.Statuses.AsNoTracking().OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToListAsync(ct);
        if (statuses.Count == 0) return Conflict("this board has no columns to put an issue in");

        var columns = Columns.From(statuses);
        var actor = await caller.ActorNameAsync(ct);
        var now = time.GetUtcNow();

        for (var attempt = 1; ; attempt++)
        {
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == request.ProjectId, ct);
            if (project is null) return NotFound();

            var bottoms = new Dictionary<int, long>();
            var results = new List<ImportedEpicDto>(docs.Count);
            var count = 0;

            async Task<EfHatchIssue> AddAsync(string type, string title, string description, PlanState state, string source, EfHatchIssue? parent)
            {
                var statusId = columns.For(state);

                // The bottom of each column is asked for once and then walked
                // down by hand. Issues added in this loop are not in the store
                // until the save at the end of it, so asking a second time
                // would hand the same rank to every card in the column.
                if (!bottoms.TryGetValue(statusId, out var rank)) rank = await ranks.BottomAsync(statusId, ct);
                bottoms[statusId] = rank + RankService.Gap;

                var issue = new EfHatchIssue
                {
                    ProjectId = project.Id,
                    Number = project.NextIssueNumber++,
                    Type = type,
                    Title = title,
                    Description = description,
                    StatusId = statusId,
                    Parent = parent,
                    Rank = rank,
                    CreatedBy = actor,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                // One event, and it is `imported` rather than `created`: the
                // trail should say this issue was copied out of a file and name
                // the file, because that is the question somebody reading it in
                // six months will actually have.
                issue.Events.Add(new EfHatchIssueEvent
                {
                    Actor = actor,
                    Kind = EfHatchIssueEvent.Imported,
                    Payload = JsonSerializer.Serialize(new { source }),
                    At = now,
                });

                db.Issues.Add(issue);
                count++;
                return issue;
            }

            foreach (var doc in docs)
            {
                var epic = await AddAsync("epic", doc.Title, doc.Description, doc.State, doc.Filename, null);

                var tasks = 0;
                foreach (var parsedStory in doc.Stories)
                {
                    var story = await AddAsync("story", parsedStory.Title, parsedStory.Description, parsedStory.State, doc.Filename, epic);

                    foreach (var parsedTask in parsedStory.Tasks)
                    {
                        await AddAsync("task", parsedTask.Title, parsedTask.Description, parsedTask.State, doc.Filename, story);
                        tasks++;
                    }
                }

                results.Add(new ImportedEpicDto(
                    doc.Filename,
                    IssueKey.Format(project.Key, epic.Number),
                    doc.Title,
                    doc.Stories.Count,
                    tasks));
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (attempt < MintAttempts)
            {
                // Somebody took a number between our read of the project and
                // our write. Drop everything this attempt staged - the failed
                // inserts included, which would otherwise be retried alongside
                // the next lot - and build the batch again.
                db.ChangeTracker.Clear();
                continue;
            }
            catch (DbUpdateException)
            {
                return Conflict("could not mint issue numbers for this import - try again");
            }

            return new ImportResultDto(results, count);
        }
    }

    /// <summary>
    /// Which column each of the parser's three states lands in.
    ///
    /// Resolved by name with a fallback, rather than by position, because the
    /// columns are the operator's - they can be renamed, reordered, and deleted
    /// from a page - and an importer that landed work in "whatever is third"
    /// would file a plan into the wrong column the day somebody added a review
    /// step.
    /// </summary>
    private sealed record Columns(int Todo, int InProgress, int Done)
    {
        public static Columns From(IReadOnlyList<EfHatchStatus> ordered)
        {
            // Done is the leftmost column that says work shipped - the flag,
            // not the name, because that flag is the one thing about a column
            // this module actually understands. Failing that, the rightmost
            // column, which is where a board puts finished work by convention.
            var done = ordered.FirstOrDefault(s => s.IsTerminal) ?? ordered[^1];

            // Todo is the column called that, and otherwise the leftmost column
            // still holding unfinished work - never the terminal one, or an
            // unchecked box would import as shipped.
            var todo = Named(ordered, "todo") ?? ordered.FirstOrDefault(s => !s.IsTerminal) ?? ordered[0];

            // A board with no "in progress" column has nowhere honest to put
            // half-finished work, so it goes where the rest of the unfinished
            // work goes rather than being rounded up to done.
            var inProgress = Named(ordered, "in progress") ?? todo;

            return new Columns(todo.Id, inProgress.Id, done.Id);
        }

        public int For(PlanState state) => state switch
        {
            PlanState.Done => Done,
            PlanState.InProgress => InProgress,
            _ => Todo,
        };

        // Matched on the letters and digits alone, so that "To Do", "todo" and
        // "TODO" are one column and not three. The shipped board writes its
        // columns the way a person would ("In Progress"), scripts/hatch.sh
        // resolves a column name by exactly this rule, and a lookup here that
        // insisted on the spacing would file every unchecked box into whatever
        // happened to be leftmost.
        private static EfHatchStatus? Named(IReadOnlyList<EfHatchStatus> ordered, string name) =>
            ordered.FirstOrDefault(s => Squash(s.Name) == Squash(name));

        private static string Squash(string name) =>
            string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }
}

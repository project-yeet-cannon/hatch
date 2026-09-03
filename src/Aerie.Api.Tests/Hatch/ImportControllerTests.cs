using System.Text;
using System.Text.Json;
using Aerie.Api.Ef;
using Aerie.Api.Modules.Hatch;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// The importer end to end: what an uploaded plan turns into on the board.
///
/// <see cref="PlanImportParserTests"/> covers the reading of a document; these
/// cover the writing of it - the hierarchy, the columns, the numbering, and the
/// one event that says where each issue came from.
/// </summary>
public class ImportControllerTests
{
    // ---- The hierarchy ----

    [Fact]
    public async Task ADocument_BecomesAnEpicWithAStoryPerPhase()
    {
        var h = await NewAsync();

        var result = await h.ImportAsync("pjm.md", Plan);

        var epic = Assert.Single(result.Epics);
        Assert.Equal("AER-1", epic.Key);
        Assert.Equal("Hatch", epic.Title);
        Assert.Equal(2, epic.StoryCount);
        Assert.Equal(3, epic.TaskCount);
        Assert.Equal(6, result.IssueCount);
    }

    [Fact]
    public async Task EachIssue_HangsUnderTheOneAboveIt()
    {
        var h = await NewAsync();
        await h.ImportAsync("pjm.md", Plan);

        var epic = h.Issue("AER-1");
        var story = h.Issue("AER-2");
        var task = h.Issue("AER-3");

        Assert.Equal(["epic", "story", "task"], new[] { epic, story, task }.Select(i => i.Type));
        Assert.Null(epic.ParentId);
        Assert.Equal(epic.Id, story.ParentId);
        Assert.Equal(story.Id, task.ParentId);
    }

    /// <summary>
    /// Depth-first, so the keys read in the order the document does: an epic,
    /// then its first phase, then that phase's tasks. Somebody scanning the
    /// board after an import should be able to follow it.
    /// </summary>
    [Fact]
    public async Task TheIssues_AreNumberedInTheOrderTheDocumentReads()
    {
        var h = await NewAsync();
        await h.ImportAsync("pjm.md", Plan);

        Assert.Equal(
            ["Hatch", "Phase 0 — Start", "Write the module", "Write the tests", "Phase 1 — Finish", "Ship it"],
            h.Db.Issues.OrderBy(i => i.Number).Select(i => i.Title).ToList());
    }

    [Fact]
    public async Task ImportedNumbers_ContinueTheProjectsOwn()
    {
        var h = await NewAsync();
        await h.Issues.CreateIssue(new IssueCreateRequest(h.ProjectId, "bug", "found something", null, null), default);

        var result = await h.ImportAsync("pjm.md", Plan);

        Assert.Equal("AER-2", Assert.Single(result.Epics).Key);
    }

    [Fact]
    public async Task TwoDocuments_BecomeTwoEpics()
    {
        var h = await NewAsync();

        var result = await h.ImportAsync(("a.md", Plan), ("b.md", "# Another\n\n## Phase 0 — Only\n\n- [ ] one thing\n"));

        Assert.Equal(["a.md", "b.md"], result.Epics.Select(e => e.Filename));
        Assert.Equal(["AER-1", "AER-7"], result.Epics.Select(e => e.Key));
    }

    // ---- Columns ----

    [Fact]
    public async Task ACheckedBox_LandsInTheTerminalColumnAndAnEmptyOneInTodo()
    {
        var h = await NewAsync();
        await h.ImportAsync("pjm.md", Plan);

        Assert.Equal(h.Done, h.Issue("AER-3").StatusId);
        Assert.Equal(h.Done, h.Issue("AER-4").StatusId);
        Assert.Equal(h.Todo, h.Issue("AER-6").StatusId);
    }

    [Fact]
    public async Task AFinishedPhase_LandsDoneAndAnUnstartedOneLandsTodo()
    {
        var h = await NewAsync();
        await h.ImportAsync("pjm.md", Plan);

        Assert.Equal(h.Done, h.Issue("AER-2").StatusId);
        Assert.Equal(h.Todo, h.Issue("AER-5").StatusId);
    }

    [Fact]
    public async Task AHalfFinishedPlan_LandsInProgress()
    {
        var h = await NewAsync();

        await h.ImportAsync("pjm.md", Plan);

        Assert.Equal(h.InProgress, h.Issue("AER-1").StatusId);
    }

    /// <summary>
    /// The columns belong to the operator: they can be renamed and reordered
    /// from a page. An importer that landed work in "whatever is third" would
    /// file a plan into the wrong column the day a review step was added, so it
    /// asks the board what its columns are called and falls back on the
    /// terminal flag.
    /// </summary>
    [Fact]
    public async Task ABoardWithDifferentColumnNames_StillTakesAnImport()
    {
        var h = await NewAsync(seedStatuses: false);
        var backlog = new EfHatchStatus { Name = "backlog", SortOrder = 10 };
        var shipped = new EfHatchStatus { Name = "shipped", SortOrder = 20, IsTerminal = true };
        h.Db.AddRange(backlog, shipped);
        await h.Db.SaveChangesAsync();

        await h.ImportAsync("pjm.md", Plan);

        Assert.Equal(backlog.Id, h.Issue("AER-6").StatusId);
        Assert.Equal(shipped.Id, h.Issue("AER-3").StatusId);
        // No "in progress" column: half-finished work goes where the rest of
        // the unfinished work goes rather than being rounded up to done.
        Assert.Equal(backlog.Id, h.Issue("AER-1").StatusId);
    }

    /// <summary>
    /// Every card in a column needs its own place in it. The ranks are handed
    /// out in memory because the issues are not in the database until the save
    /// at the end - asking the column for its bottom a second time would give
    /// every card the same number, and the board would order them arbitrarily.
    /// </summary>
    [Fact]
    public async Task CardsLandingInOneColumn_GetDistinctRanks()
    {
        var h = await NewAsync();
        await h.ImportAsync("pjm.md", Plan);

        var done = h.Db.Issues.Where(i => i.StatusId == h.Done).Select(i => i.Rank).ToList();

        Assert.Equal(3, done.Count);
        Assert.Equal(done.Count, done.Distinct().Count());
    }

    /// <summary>
    /// An import appends. Landing a plan on top of work already in a column
    /// would reorder a board the operator arranged by hand, and the plan being
    /// imported is by definition the newer arrival.
    /// </summary>
    [Fact]
    public async Task ImportedCards_SitBelowWhatIsAlreadyInTheColumn()
    {
        var h = await NewAsync();
        await h.Issues.CreateIssue(new IssueCreateRequest(h.ProjectId, "bug", "already here", null, null), default);
        await h.Issues.MoveIssue("AER-1", new IssueMoveRequest(h.Todo, null, null), default);
        var sitting = h.Issue("AER-1").Rank;

        await h.ImportAsync("pjm.md", "# Plan\n\n## Phase 0 — Start\n\n- [ ] a thing\n");

        Assert.All(
            h.Db.Issues.AsNoTracking().Where(i => i.StatusId == h.Todo && i.Number > 1).Select(i => i.Rank).ToList(),
            rank => Assert.True(rank > sitting, $"rank {rank} is not below {sitting}"));
    }

    // ---- The trail ----

    [Fact]
    public async Task EveryImportedIssue_CarriesOneImportedEventNamingTheFile()
    {
        var h = await NewAsync();
        await h.ImportAsync("pjm.md", Plan);

        var events = h.Db.IssueEvents.ToList();

        Assert.Equal(6, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(EfHatchIssueEvent.Imported, e.Kind);
            Assert.Equal("Nathan", e.Actor);
            Assert.Equal("pjm.md", JsonDocument.Parse(e.Payload!).RootElement.GetProperty("source").GetString());
        });
    }

    [Fact]
    public async Task AnImportedDescription_SaysWhichFileItCameFrom()
    {
        var h = await NewAsync();
        await h.ImportAsync("pjm.md", Plan);

        Assert.EndsWith("_Imported from `pjm.md`_", h.Issue("AER-1").Description);
    }

    // ---- Refusals ----

    [Fact]
    public async Task AnImportIntoAProjectThatIsNotThere_Is404()
    {
        var h = await NewAsync();
        var docs = await h.PreviewAsync(("pjm.md", Plan));

        var result = await h.Import.Import(new ImportRequest(9999, docs), default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task AnImportOfNothing_IsRefused()
    {
        var h = await NewAsync();

        var result = await h.Import.Import(new ImportRequest(h.ProjectId, []), default);

        Assert.Contains("nothing to import", Reason(result.Result));
    }

    [Fact]
    public async Task ABoardWithNoColumns_HasNowhereToPutAnImport()
    {
        var h = await NewAsync(seedStatuses: false);
        var docs = await h.PreviewAsync(("pjm.md", Plan));

        var result = await h.Import.Import(new ImportRequest(h.ProjectId, docs), default);

        Assert.Contains("no columns", Reason(result.Result));
    }

    // ---- Preview ----

    [Fact]
    public async Task Preview_ReadsTheFilesAndWritesNothing()
    {
        var h = await NewAsync();

        var docs = await h.PreviewAsync(("pjm.md", Plan));

        Assert.Equal("Hatch", Assert.Single(docs).Title);
        Assert.Empty(h.Db.Issues);
    }

    [Fact]
    public async Task AFileThatIsNotMarkdown_IsRefused()
    {
        var h = await NewAsync();

        var result = await h.Import.Preview(Files(("notes.txt", "# Plan")), default);

        Assert.Contains("is not a .md file", Reason(result.Result));
    }

    [Fact]
    public async Task AnUploadOfNothing_IsRefused()
    {
        var h = await NewAsync();

        var result = await h.Import.Preview(new FormFileCollection(), default);

        Assert.Contains("no files", Reason(result.Result));
    }

    [Fact]
    public async Task AFileLargerThanTheCap_IsRefused()
    {
        var h = await NewAsync();

        var result = await h.Import.Preview(Files(("huge.md", new string('x', ImportController.MaxFileBytes + 1))), default);

        Assert.Contains("larger than", Reason(result.Result));
    }

    // ---- Harness ----

    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One epic, two phases, three boxes - one phase finished and one not, so
    /// all three roll-ups appear in the same import.
    /// </summary>
    private const string Plan = """
        # Hatch

        The house project tracker.

        ## Phase 0 — Start

        - [x] Write the module
        - [x] Write the tests

        ## Phase 1 — Finish

        - [ ] Ship it
        """;

    private sealed class Harness
    {
        public required HatchContext Db { get; init; }
        public required ImportController Import { get; init; }
        public required IssuesController Issues { get; init; }
        public required int ProjectId { get; init; }
        public int Todo { get; set; }
        public int InProgress { get; set; }
        public int Done { get; set; }

        public async Task<IReadOnlyList<ParsedEpic>> PreviewAsync(params (string Name, string Content)[] files) =>
            Value(await Import.Preview(Files(files), default));

        public Task<ImportResultDto> ImportAsync(string name, string content) => ImportAsync((name, content));

        public async Task<ImportResultDto> ImportAsync(params (string Name, string Content)[] files)
        {
            var docs = await PreviewAsync(files);
            return Value(await Import.Import(new ImportRequest(ProjectId, docs), default));
        }

        /// <summary>The row behind a display key, read back untracked - what actually landed.</summary>
        public EfHatchIssue Issue(string key)
        {
            IssueKey.TryParse(key, out var projectKey, out var number);
            return Db.Issues.AsNoTracking().Single(i => i.Project!.Key == projectKey && i.Number == number);
        }
    }

    private static async Task<Harness> NewAsync(bool seedStatuses = true)
    {
        var db = new HatchContext(
            new DbContextOptionsBuilder<HatchContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var aerie = new EfHatchProject { Key = "AER", Name = "Aerie", CreatedAt = Now };
        db.Add(aerie);

        var inbox = new EfHatchStatus { Name = "inbox", SortOrder = 10 };
        var todo = new EfHatchStatus { Name = "todo", SortOrder = 20 };
        var doing = new EfHatchStatus { Name = "in progress", SortOrder = 30 };
        var done = new EfHatchStatus { Name = "done", SortOrder = 40, IsTerminal = true };
        if (seedStatuses) db.AddRange(inbox, todo, doing, done);

        await db.SaveChangesAsync();

        var time = new FakeTimeProvider(Now);
        var caller = new StubCallerIdentity { Person = new EfPerson { Name = "Nathan", CreatedAt = Now, UpdatedAt = Now } };
        var ranks = new RankService(db);

        return new Harness
        {
            Db = db,
            Import = new ImportController(db, new PlanImportParser(), ranks, caller, time),
            Issues = new IssuesController(db, ranks, caller, time),
            ProjectId = aerie.Id,
            Todo = todo.Id,
            InProgress = doing.Id,
            Done = done.Id,
        };
    }

    /// <summary>An upload, as the model binder would hand it to the controller.</summary>
    private static FormFileCollection Files(params (string Name, string Content)[] files)
    {
        var collection = new FormFileCollection();

        foreach (var (name, content) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            collection.Add(new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name));
        }

        return collection;
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

    private static T Value<T>(ActionResult<T> result) =>
        result.Value ?? throw new InvalidOperationException($"expected a value, got {Reason(result.Result)}");

    /// <summary>The plain-text reason on a refusal - what the UI puts on screen.</summary>
    private static string Reason(IActionResult? result) => result switch
    {
        ObjectResult o => $"{o.StatusCode}: {o.Value}",
        StatusCodeResult s => s.StatusCode.ToString(),
        null => "no result",
        _ => result.GetType().Name,
    };
}

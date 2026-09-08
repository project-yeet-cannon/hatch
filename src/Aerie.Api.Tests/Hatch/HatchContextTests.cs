using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using Aerie.Api.Modules.Hatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// The model's shape, asserted where it is cheap to assert. Most of what these
/// pin - the schema, the unique indexes, the delete behaviours - is invisible
/// until it is wrong in production, and each one encodes a decision from
/// docs/hatch.md that a later refactor could quietly reverse.
/// </summary>
public class HatchContextTests
{
    [Fact]
    public void EverythingHatchOwns_LivesInTheHatchSchema()
    {
        using var db = NewContext();

        var strays = db.Model.GetEntityTypes()
            .Where(e => e.GetSchema() != HatchContext.Schema)
            .Select(e => e.ClrType.Name)
            .ToList();

        Assert.Empty(strays);
        Assert.Equal("hatch", HatchContext.Schema);
    }

    [Fact]
    public void TheModuleOwnsNineTables()
    {
        using var db = NewContext();

        Assert.Equal(
            ["Comments", "IssueDependencies", "IssueEvents", "Issues", "Playbooks", "Projects", "Runners", "Statuses", "WorkLogEntries"],
            db.Model.GetEntityTypes().Select(TableName).OrderBy(n => n, StringComparer.Ordinal));
    }

    // ---- Round trips ----

    [Fact]
    public async Task AnIssue_RoundTripsWithItsProjectStatusCommentsAndEvents()
    {
        using var db = NewContext();
        var (project, status) = await SeedAsync(db);

        var issue = new EfHatchIssue
        {
            ProjectId = project.Id,
            Number = 1,
            Type = "story",
            Title = "Hatch the board",
            Description = "# heading\n\nmarkdown, stored raw",
            StatusId = status.Id,
            Rank = 1024,
            CreatedBy = "operator",
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        db.Issues.Add(issue);
        await db.SaveChangesAsync();

        db.Comments.Add(new EfHatchComment { IssueId = issue.Id, Author = "operator", Body = "started", CreatedAt = Now });
        db.IssueEvents.Add(new EfHatchIssueEvent
        {
            IssueId = issue.Id,
            Actor = "operator",
            Kind = EfHatchIssueEvent.Created,
            Payload = """{"to":"story"}""",
            At = Now,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var loaded = await db.Issues
            .Include(i => i.Project)
            .Include(i => i.Status)
            .Include(i => i.Comments)
            .Include(i => i.Events)
            .SingleAsync();

        Assert.Equal("AER", loaded.Project!.Key);
        Assert.Equal("inbox", loaded.Status!.Name);
        Assert.Equal("# heading\n\nmarkdown, stored raw", loaded.Description);
        Assert.Equal("started", Assert.Single(loaded.Comments).Body);
        Assert.Equal(EfHatchIssueEvent.Created, Assert.Single(loaded.Events).Kind);
    }

    /// <summary>
    /// The display key is computed, never stored - so nothing in the model may
    /// hold a column that could disagree with <c>Project.Key + "-" + Number</c>.
    /// </summary>
    [Fact]
    public void AnIssue_StoresNoKeyColumn()
    {
        using var db = NewContext();

        var names = db.Model.FindEntityType(typeof(EfHatchIssue))!.GetProperties().Select(p => p.Name);

        Assert.DoesNotContain("Key", names);
    }

    /// <summary>
    /// An epic and a story may both be parented; a task may hang under a story.
    /// The self-reference has to be a real relationship in the model, not a
    /// bare column, or the cycle walk in the controller has nothing to follow.
    /// </summary>
    [Fact]
    public async Task AnIssue_MayParentAnotherIssueInTheSameTable()
    {
        using var db = NewContext();
        var (project, status) = await SeedAsync(db);

        var epic = NewIssue(project.Id, status.Id, 1, "epic", "The plan");
        db.Issues.Add(epic);
        await db.SaveChangesAsync();

        var story = NewIssue(project.Id, status.Id, 2, "story", "Phase 0");
        story.ParentId = epic.Id;
        db.Issues.Add(story);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reloaded = await db.Issues.Include(i => i.Children).SingleAsync(i => i.Number == 1);
        Assert.Equal("Phase 0", Assert.Single(reloaded.Children).Title);
    }

    // ---- Constraints the plan named ----

    [Theory]
    [InlineData(typeof(EfHatchProject), new[] { nameof(EfHatchProject.Key) })]
    [InlineData(typeof(EfHatchStatus), new[] { nameof(EfHatchStatus.Name) })]
    [InlineData(typeof(EfHatchIssue), new[] { nameof(EfHatchIssue.ProjectId), nameof(EfHatchIssue.Number) })]
    [InlineData(typeof(EfHatchIssueDependency),
        new[] { nameof(EfHatchIssueDependency.IssueId), nameof(EfHatchIssueDependency.DependsOnId) })]
    public void TheUniquenessTheBoardDependsOn_IsInTheDatabase(Type entity, string[] properties)
    {
        using var db = NewContext();

        var indexes = db.Model.FindEntityType(entity)!.GetIndexes()
            .Where(i => i.IsUnique)
            .Select(i => i.Properties.Select(p => p.Name).ToArray());

        Assert.Contains(indexes, i => i.SequenceEqual(properties));
    }

    /// <summary>
    /// The numbering safety net. The retry loop in the create path is the
    /// first line of defence and this index is the second - without it a lost
    /// race writes a second AER-12 and nobody finds out until two links point
    /// at different tickets.
    /// </summary>
    [Fact]
    public void NextIssueNumber_IsAConcurrencyToken()
    {
        using var db = NewContext();

        var property = db.Model.FindEntityType(typeof(EfHatchProject))!
            .FindProperty(nameof(EfHatchProject.NextIssueNumber))!;

        Assert.True(property.IsConcurrencyToken);
    }

    /// <summary>
    /// A project or a status is deleted through an endpoint that refuses while
    /// issues still hold it. Cascade here would make that guard the only thing
    /// standing between a mis-click and an emptied board.
    /// </summary>
    [Theory]
    [InlineData(nameof(EfHatchIssue.ProjectId), DeleteBehavior.Restrict)]
    [InlineData(nameof(EfHatchIssue.StatusId), DeleteBehavior.Restrict)]
    [InlineData(nameof(EfHatchIssue.ParentId), DeleteBehavior.SetNull)]
    public void AnIssuesForeignKeys_RefuseOrOutdentRatherThanCascade(string foreignKey, DeleteBehavior expected)
    {
        using var db = NewContext();

        Assert.Equal(expected, ForeignKeyOn(db, typeof(EfHatchIssue), foreignKey).DeleteBehavior);
    }

    /// <summary>
    /// The accepted MVP gap, written down as a test so it stays a decision: a
    /// deleted issue takes its comments, its audit trail and its work log with
    /// it.
    /// </summary>
    [Theory]
    [InlineData(typeof(EfHatchComment), nameof(EfHatchComment.IssueId))]
    [InlineData(typeof(EfHatchIssueEvent), nameof(EfHatchIssueEvent.IssueId))]
    [InlineData(typeof(EfHatchWorkLogEntry), nameof(EfHatchWorkLogEntry.IssueId))]
    public void CommentsAndEvents_CascadeWithTheirIssue(Type entity, string foreignKey)
    {
        using var db = NewContext();

        Assert.Equal(DeleteBehavior.Cascade, ForeignKeyOn(db, entity, foreignKey).DeleteBehavior);
    }

    /// <summary>
    /// An edge has no meaning without either issue, so deleting either takes
    /// it - in both directions. Pinned as model metadata rather than exercised,
    /// because the in-memory provider does not enforce a foreign key and every
    /// other delete behaviour in this file is pinned the same way.
    /// </summary>
    [Theory]
    [InlineData(nameof(EfHatchIssueDependency.IssueId))]
    [InlineData(nameof(EfHatchIssueDependency.DependsOnId))]
    public void DependencyEdges_CascadeWithEitherIssue(string foreignKey)
    {
        using var db = NewContext();

        Assert.Equal(
            DeleteBehavior.Cascade,
            ForeignKeyOn(db, typeof(EfHatchIssueDependency), foreignKey).DeleteBehavior);
    }

    [Fact]
    public void AnEventsPayload_IsJsonb()
    {
        using var db = NewContext();

        var payload = db.Model.FindEntityType(typeof(EfHatchIssueEvent))!
            .FindProperty(nameof(EfHatchIssueEvent.Payload))!;

        Assert.Equal("jsonb", payload.FindAnnotation(RelationalAnnotationNames.ColumnType)?.Value);
    }

    [Fact]
    public void AWorkLogEntry_IsUniquePerSessionAndIssue()
    {
        using var db = NewContext();

        var entry = db.Model.FindEntityType(typeof(EfHatchWorkLogEntry))!;

        // What makes the dispatcher's post idempotent rather than a second row
        // every time a run is re-run. The endpoint reads first and updates in
        // place; this is the backstop under it.
        var unique = entry.GetIndexes().Single(i => i.IsUnique);
        Assert.Equal(
            [nameof(EfHatchWorkLogEntry.IssueId), nameof(EfHatchWorkLogEntry.SessionId)],
            unique.Properties.Select(p => p.Name));

        // And the page's one query, which is this issue's entries newest first.
        Assert.Contains(
            entry.GetIndexes(),
            i => !i.IsUnique
                 && i.Properties.Select(p => p.Name)
                     .SequenceEqual([nameof(EfHatchWorkLogEntry.IssueId), nameof(EfHatchWorkLogEntry.EndedAt)]));
    }

    [Fact]
    public void AWorkLogEntry_KeepsEightPlacesOfDollarsAndItsBreakdownAsJsonb()
    {
        using var db = NewContext();

        var entry = db.Model.FindEntityType(typeof(EfHatchWorkLogEntry))!;

        // Two places would round a night of cheap sessions to nothing.
        var cost = entry.FindProperty(nameof(EfHatchWorkLogEntry.CostUsd))!;
        Assert.Equal(18, cost.GetPrecision());
        Assert.Equal(8, cost.GetScale());

        var models = entry.FindProperty(nameof(EfHatchWorkLogEntry.ModelUsage))!;
        Assert.Equal("jsonb", models.FindAnnotation(RelationalAnnotationNames.ColumnType)?.Value);
    }

    // ---- Key format ----

    [Theory]
    [InlineData("AER", true)]
    [InlineData("OPS2", true)]
    [InlineData("ABCDEF", true)]
    [InlineData("A", false)]           // one character is not a namespace
    [InlineData("ABCDEFG", false)]     // seven is past the column
    [InlineData("aer", false)]         // keys are shouted
    [InlineData("1AB", false)]         // must start with a letter
    [InlineData("AE-R", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AProjectKey_IsShortShoutedAndLetterLed(string? key, bool valid)
    {
        Assert.Equal(valid, EfHatchProject.IsValidKey(key));
    }

    [Theory]
    [InlineData("epic", true)]
    [InlineData("story", true)]
    [InlineData("task", true)]
    [InlineData("bug", true)]
    [InlineData("Epic", false)]
    [InlineData("chore", false)]
    [InlineData(null, false)]
    public void AnIssueType_IsOneOfFour(string? type, bool valid)
    {
        Assert.Equal(valid, EfHatchIssue.IsValidType(type));
    }

    // ---- Helpers ----

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The table an entity maps to, read off its <c>[Table]</c> attribute
    /// rather than through <c>GetTableName()</c>: these tests run on the
    /// in-memory provider, which has no relational model to ask and answers
    /// with the CLR type name instead.
    /// </summary>
    private static string TableName(IReadOnlyEntityType entity) =>
        entity.ClrType.GetCustomAttribute<TableAttribute>()!.Name!;

    private static IForeignKey ForeignKeyOn(HatchContext db, Type entity, string property) =>
        db.Model.FindEntityType(entity)!.GetForeignKeys()
            .Single(fk => fk.Properties.Count == 1 && fk.Properties[0].Name == property);

    internal static EfHatchIssue NewIssue(int projectId, int statusId, int number, string type, string title) => new()
    {
        ProjectId = projectId,
        Number = number,
        Type = type,
        Title = title,
        StatusId = statusId,
        Rank = number * 1024L,
        CreatedBy = "operator",
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static async Task<(EfHatchProject Project, EfHatchStatus Status)> SeedAsync(HatchContext db)
    {
        var project = new EfHatchProject { Key = "AER", Name = "Aerie", CreatedAt = Now };
        var status = new EfHatchStatus { Name = "inbox", SortOrder = 10 };
        db.Projects.Add(project);
        db.Statuses.Add(status);
        await db.SaveChangesAsync();
        return (project, status);
    }

    private static HatchContext NewContext() =>
        new(new DbContextOptionsBuilder<HatchContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}

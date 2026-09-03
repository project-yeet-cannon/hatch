using Aerie.Api.Modules.Hatch;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// The importer's reading of a plan document. These are the tests that matter
/// most in Phase 5: the parser is pure, so everything it gets wrong it gets
/// wrong silently and identically in the preview the operator approves.
/// </summary>
public class PlanImportParserTests
{
    private static readonly PlanImportParser Parser = new();

    // ---- The shape ----

    [Fact]
    public void TheH1_IsTheEpicTitle()
    {
        var epic = Parser.Parse("pjm.md", "# Hatch — the house project tracker\n\nSome prose.\n");

        Assert.Equal("Hatch — the house project tracker", epic.Title);
    }

    /// <summary>A document with no heading is still a document. The file it arrived as names it.</summary>
    [Fact]
    public void AFileWithNoH1_IsTitledAfterTheFile()
    {
        var epic = Parser.Parse("delivery-architecture.md", "Some prose with no heading at all.\n");

        Assert.Equal("delivery-architecture", epic.Title);
    }

    [Fact]
    public void EachPhaseHeading_OpensAStory()
    {
        var epic = Parser.Parse("plan.md", Sample);

        Assert.Equal(
            ["Phase 0 — Backend skeleton", "Phase 1 — The API", "phase 2 - lower case counts"],
            epic.Stories.Select(s => s.Title));
    }

    [Fact]
    public void CheckboxesUnderAPhase_AreItsTasks()
    {
        var epic = Parser.Parse("plan.md", Sample);

        Assert.Equal(["Create the module", "Scaffold the migration"], epic.Stories[0].Tasks.Select(t => t.Title));
    }

    /// <summary>
    /// A plan wraps a long task across several indented lines. Those lines are
    /// the task, not the phase's prose - getting this wrong turns every task in
    /// a real plan into a fragment and every phase description into a pile of
    /// orphaned clauses.
    /// </summary>
    [Fact]
    public void AWrappedCheckbox_IsOneTask()
    {
        var epic = Parser.Parse("plan.md", Sample);

        var task = epic.Stories[1].Tasks[0];
        Assert.Equal("Write the controller with the retry loop exactly as specified, and its tests", task.Title);
        Assert.Single(epic.Stories[1].Tasks);
    }

    /// <summary>
    /// Nested boxes flatten, in document order. A sub-task is still a thing to
    /// do, and a board with no tree view has nowhere to draw the nesting.
    /// </summary>
    [Fact]
    public void NestedCheckboxes_FlattenInOrder()
    {
        var epic = Parser.Parse("plan.md", """
            # Plan

            ## Phase 0 — Start

            - [x] The outer one
              - [ ] The inner one
              - [ ] The other inner one
            - [ ] Back out again
            """);

        Assert.Equal(
            ["The outer one", "The inner one", "The other inner one", "Back out again"],
            epic.Stories[0].Tasks.Select(t => t.Title));
    }

    [Fact]
    public void ProseUnderAPhase_IsTheStorysDescription()
    {
        var epic = Parser.Parse("plan.md", Sample);

        Assert.Contains("The module exists and migrates.", epic.Stories[0].Description);
        Assert.Contains("Verify: `make build`.", epic.Stories[0].Description);
    }

    // ---- What stays with the epic ----

    [Fact]
    public void EverythingBeforeTheFirstPhase_IsTheEpicDescription()
    {
        var epic = Parser.Parse("plan.md", Sample);

        Assert.Contains("Hatch replaces markdown files.", epic.Description);
    }

    /// <summary>
    /// "Decisions", "Non-goals": context for the whole plan rather than a unit
    /// of work. They keep their heading and stay in the epic, wherever in the
    /// file they sat - including after the phases have started.
    /// </summary>
    [Fact]
    public void AnH2ThatIsNotAPhase_StaysInTheEpic()
    {
        var epic = Parser.Parse("plan.md", Sample);

        Assert.Contains("## Decisions", epic.Description);
        Assert.Contains("Rank is a long with 1024-gaps.", epic.Description);
        Assert.Contains("## Afterwards", epic.Description);
        Assert.Contains("This trails the phases.", epic.Description);
        Assert.DoesNotContain("Afterwards", epic.Stories.Select(s => s.Title));
    }

    /// <summary>The title is not repeated as the first line of the description it titles.</summary>
    [Fact]
    public void TheH1_IsNotAlsoTheDescription()
    {
        var epic = Parser.Parse("plan.md", Sample);

        Assert.DoesNotContain("# Hatch", epic.Description);
    }

    [Fact]
    public void ADocumentWithNoPhases_IsAnEpicOnItsOwn()
    {
        var epic = Parser.Parse("ethos.md", "# Aerie Ethos\n\nNothing in this repo may be true of exactly one installation.\n");

        Assert.Empty(epic.Stories);
        Assert.Contains("Nothing in this repo", epic.Description);
        Assert.Equal(PlanState.Todo, epic.State);
    }

    // ---- Checked state ----

    [Fact]
    public void ACheckedBox_IsDoneAndAnEmptyOneIsNot()
    {
        var epic = Parser.Parse("plan.md", Sample);

        Assert.Equal([PlanState.Done, PlanState.Done], epic.Stories[0].Tasks.Select(t => t.State));
        Assert.Equal([PlanState.Todo], epic.Stories[1].Tasks.Select(t => t.State));
    }

    [Theory]
    [InlineData("- [x] done", PlanState.Done)]
    [InlineData("- [X] done", PlanState.Done)]
    [InlineData("- [ ] not done", PlanState.Todo)]
    [InlineData("* [x] done", PlanState.Done)]
    [InlineData("+ [ ] not done", PlanState.Todo)]
    public void EveryWayOfWritingABox_IsRead(string line, PlanState expected)
    {
        var epic = Parser.Parse("plan.md", $"# Plan\n\n## Phase 0\n\n{line}\n");

        Assert.Equal(expected, Assert.Single(epic.Stories[0].Tasks).State);
    }

    [Fact]
    public void AStory_RollsUpItsTasks()
    {
        var epic = Parser.Parse("plan.md", Sample);

        // All checked, one unchecked, none at all.
        Assert.Equal(
            [PlanState.Done, PlanState.Todo, PlanState.Todo],
            epic.Stories.Select(s => s.State));
    }

    [Fact]
    public void AStoryWithSomeBoxesChecked_IsInProgress()
    {
        var epic = Parser.Parse("plan.md", "# Plan\n\n## Phase 0\n\n- [x] one\n- [ ] two\n");

        Assert.Equal(PlanState.InProgress, epic.Stories[0].State);
        Assert.Equal(PlanState.InProgress, epic.State);
    }

    [Fact]
    public void AnEpic_IsDoneOnlyWhenEveryStoryIs()
    {
        var all = Parser.Parse("plan.md", "# Plan\n\n## Phase 0\n\n- [x] one\n\n## Phase 1\n\n- [x] two\n");
        var some = Parser.Parse("plan.md", "# Plan\n\n## Phase 0\n\n- [x] one\n\n## Phase 1\n\n- [ ] two\n");

        Assert.Equal(PlanState.Done, all.State);
        Assert.Equal(PlanState.InProgress, some.State);
    }

    /// <summary>
    /// A phase with no boxes is a phase nobody has begun, not a phase that
    /// shipped - and an epic made of those is the same. The alternative reading
    /// (nothing outstanding, therefore done) would import an untouched plan
    /// straight into the done column.
    /// </summary>
    [Fact]
    public void AnEmptyPhase_IsNotDone()
    {
        var epic = Parser.Parse("plan.md", "# Plan\n\n## Phase 0\n\nJust prose.\n");

        Assert.Equal(PlanState.Todo, epic.Stories[0].State);
        Assert.Equal(PlanState.Todo, epic.State);
    }

    /// <summary>
    /// A box with nothing in it is a formatting artifact rather than a thing to
    /// do - and an issue with no title is not something the API would take.
    /// </summary>
    [Fact]
    public void AnEmptyCheckbox_IsNotATask()
    {
        var epic = Parser.Parse("plan.md", "# Plan\n\n## Phase 0\n\n- [ ]\n- [x] a real one\n");

        Assert.Equal(["a real one"], epic.Stories[0].Tasks.Select(t => t.Title));
    }

    // ---- Provenance ----

    [Fact]
    public void EveryDescription_SaysWhereItCameFrom()
    {
        var epic = Parser.Parse("pjm.md", Sample);

        Assert.EndsWith("_Imported from `pjm.md`_", epic.Description);
        Assert.All(epic.Stories, s => Assert.EndsWith("_Imported from `pjm.md`_", s.Description));
        Assert.All(epic.Stories.SelectMany(s => s.Tasks), t => Assert.EndsWith("_Imported from `pjm.md`_", t.Description));
    }

    /// <summary>
    /// A browser sending a path, or somebody typing one, must not be able to
    /// write a directory into a description or an event payload.
    /// </summary>
    [Fact]
    public void APathedFilename_IsReducedToItsFile()
    {
        var epic = Parser.Parse("docs/plans/pjm.md", "# Plan\n");

        Assert.Equal("pjm.md", epic.Filename);
        Assert.EndsWith("_Imported from `pjm.md`_", epic.Description);
    }

    // ---- Limits ----

    [Fact]
    public void ATaskLongerThanATitle_IsClippedAndKeptWholeInItsDescription()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 200));
        var epic = Parser.Parse("plan.md", $"# Plan\n\n## Phase 0\n\n- [ ] {text}\n");

        var task = Assert.Single(epic.Stories[0].Tasks);
        Assert.True(task.Title.Length <= EfHatchIssue.MaxTitleLength);
        Assert.EndsWith("…", task.Title);
        Assert.Contains(text, task.Description);
    }

    // ---- This plan's own shape ----

    /// <summary>
    /// The document that specified this parser, parsed by it. Phase 5's
    /// acceptance test in one line: a plan written for a person reads as the
    /// tree it describes.
    /// </summary>
    [Fact]
    public void ThisPlansOwnShape_ParsesToItsPhases()
    {
        var epic = Parser.Parse("pjm.md", """
            # Hatch — the house project tracker

            Aerie's projects are currently managed by juggling markdown files.

            ## How to work this plan

            - **One checkbox = one landable change.**

            ## Phase 0 — Backend skeleton: module, schema, seeded statuses

            The `Hatch` module exists, migrates, and holds the seeded status rows.

            - [x] Create `src/Aerie.Api/Modules/Hatch/Entities.cs` with the five entities
                  exactly as the Domain model section above specifies, and
                  `src/Aerie.Api/Modules/Hatch/HatchContext.cs`.
            - [x] Create `src/Aerie.Api/Modules/Hatch/HatchDesignTimeFactory.cs`
                  (three lines, mirror `QuillDesignTimeFactory.cs`).

            Verify: `make build`, `make test-api`.

            ## Phase 5 — Plans importer

            The `docs/plans/` directory becomes uploadable.

            - [ ] Create `src/Aerie.Api/Modules/Hatch/PlanImportParser.cs`: pure function
                  from `(filename, content)` to a `ParsedEpic` tree DTO.
            - [ ] Build the Import page in `apps/hatch`.
            """);

        Assert.Equal("Hatch — the house project tracker", epic.Title);
        Assert.Contains("## How to work this plan", epic.Description);
        Assert.Equal(2, epic.Stories.Count);

        var phase0 = epic.Stories[0];
        Assert.Equal("Phase 0 — Backend skeleton: module, schema, seeded statuses", phase0.Title);
        Assert.Equal(2, phase0.Tasks.Count);
        Assert.Equal(PlanState.Done, phase0.State);
        Assert.Contains("The `Hatch` module exists", phase0.Description);
        Assert.Contains("`QuillDesignTimeFactory.cs`", phase0.Tasks[1].Title);

        var phase5 = epic.Stories[1];
        Assert.Equal(2, phase5.Tasks.Count);
        Assert.Equal(PlanState.Todo, phase5.State);
        Assert.Equal("Build the Import page in `apps/hatch`.", phase5.Tasks[1].Title);

        Assert.Equal(PlanState.InProgress, epic.State);
    }

    /// <summary>
    /// A plan with the shapes that matter: phases in three casings, a non-phase
    /// H2 before and after them, a wrapped checkbox, and a phase with no boxes.
    /// </summary>
    private const string Sample = """
        # Hatch

        Hatch replaces markdown files.

        ## Decisions

        Rank is a long with 1024-gaps.

        ## Phase 0 — Backend skeleton

        The module exists and migrates.

        - [x] Create the module
        - [x] Scaffold the migration

        Verify: `make build`.

        ## Phase 1 — The API

        - [ ] Write the controller with the retry loop exactly as specified,
              and its tests

        ## phase 2 - lower case counts

        Nothing to do here yet.

        ## Afterwards

        This trails the phases.
        """;
}

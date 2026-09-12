namespace Hatch.Cli.Tests;

/// <summary>
/// What the loop reads to know it changed underneath itself, and what it hands
/// to the version that comes back.
/// </summary>
public sealed class RestartTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hatch-self-").FullName;

    public RestartTests()
    {
        Put("scripts/hatch.sh", "#!/usr/bin/env bash\n");
        Put("src/Hatch.Cli/GoToWork.cs", "// the loop\n");
        Put("src/Hatch.Cli/Program.cs", "// the entry point\n");
        Put("src/Hatch.Contracts/Dtos.cs", "// the wire\n");

        // Everything the loop is not: another project's source, and its own
        // build output.
        Put("src/Hatch.Api/Program.cs", "// somebody else's\n");
        Put("src/Hatch.Cli/bin/Release/net10.0/hatch-runner.dll", "compiled\n");
        Put("src/Hatch.Cli/obj/project.assets.json", "{}\n");
    }

    private void Put(string relative, string body)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
    }

    private SelfPrint Take() => new LoopSource(_root).Take();

    [Fact]
    public void A_tree_that_did_not_change_prints_the_same_twice()
    {
        var first = Take();
        var second = Take();

        Assert.Equal(first.Digest, second.Digest);
        Assert.Empty(second.ChangedFrom(first));
    }

    [Fact]
    public void It_reads_the_loop_and_the_wire_and_the_script_and_nothing_else()
    {
        var print = Take();

        Assert.Contains("scripts/hatch.sh", print.Files.Keys);
        Assert.Contains("src/Hatch.Cli/GoToWork.cs", print.Files.Keys);
        Assert.Contains("src/Hatch.Contracts/Dtos.cs", print.Files.Keys);

        // Another project's source is not this loop, and a change to it is not
        // one this loop has to come back for.
        Assert.DoesNotContain("src/Hatch.Api/Program.cs", print.Files.Keys);
    }

    [Fact]
    public void Build_output_is_not_source_and_does_not_enter_into_it()
    {
        var before = Take();

        // The compile a restart itself causes. If this counted, a loop that had
        // just been rebuilt would restart forever and do nothing else.
        Put("src/Hatch.Cli/bin/Release/net10.0/hatch-runner.dll", "compiled again\n");
        Put("src/Hatch.Cli/obj/Release/net10.0/GoToWork.g.cs", "// generated\n");

        var after = Take();

        Assert.Equal(before.Digest, after.Digest);
        Assert.Empty(after.ChangedFrom(before));
    }

    [Fact]
    public void An_edited_file_changes_the_print_and_is_named()
    {
        var before = Take();
        Put("src/Hatch.Cli/GoToWork.cs", "// the loop, improved\n");
        var after = Take();

        Assert.NotEqual(before.Digest, after.Digest);
        Assert.Equal(["src/Hatch.Cli/GoToWork.cs"], after.ChangedFrom(before));
    }

    [Fact]
    public void An_added_file_changes_the_print_and_is_named()
    {
        var before = Take();
        Put("src/Hatch.Cli/Restart.cs", "// new\n");
        var after = Take();

        Assert.NotEqual(before.Digest, after.Digest);
        Assert.Equal(["src/Hatch.Cli/Restart.cs"], after.ChangedFrom(before));
    }

    [Fact]
    public void A_deleted_file_changes_the_print_and_is_named()
    {
        var before = Take();
        File.Delete(Path.Combine(_root, "src", "Hatch.Cli", "Program.cs"));
        var after = Take();

        Assert.NotEqual(before.Digest, after.Digest);
        Assert.Equal(["src/Hatch.Cli/Program.cs"], after.ChangedFrom(before));
    }

    [Fact]
    public void The_script_that_launches_the_loop_is_part_of_the_loop()
    {
        // It resolves the runner and reads the settings, so a night that
        // improved it is a night running the old one until somebody stops it.
        var before = Take();
        Put("scripts/hatch.sh", "#!/usr/bin/env bash\n# and a supervisor\n");

        Assert.Equal(["scripts/hatch.sh"], Take().ChangedFrom(before));
    }

    [Fact]
    public void Several_changes_are_all_named_and_in_order()
    {
        var before = Take();
        Put("src/Hatch.Cli/Program.cs", "// changed\n");
        Put("scripts/hatch.sh", "# changed\n");

        Assert.Equal(["scripts/hatch.sh", "src/Hatch.Cli/Program.cs"], Take().ChangedFrom(before));
    }

    [Fact]
    public void A_tree_that_is_not_there_prints_nothing_rather_than_throwing()
    {
        var print = new LoopSource(Path.Combine(_root, "nowhere")).Take();

        Assert.Empty(print.Files);
    }

    [Fact]
    public void The_nights_totals_go_there_and_come_back()
    {
        var path = Path.Combine(_root, "night.json");
        var state = new NightState
        {
            Runs = 4,
            Spent = 12.5m,
            Started = new DateTimeOffset(2026, 9, 7, 22, 0, 0, TimeSpan.Zero),
            Fails = 1,
            Restarts = 2,
            UntilAt = new DateTimeOffset(2026, 9, 8, 6, 0, 0, TimeSpan.Zero),
            Moved = ["hatch:   moved    AER-1  landed"],
            Stalled = ["hatch:   stalled  AER-2  asked a question"],
        };

        Assert.True(state.Write(path));

        var back = NightState.Read(path);
        Assert.NotNull(back);
        Assert.Equal(4, back.Runs);
        Assert.Equal(12.5m, back.Spent);
        Assert.Equal(state.Started, back.Started);
        Assert.Equal(1, back.Fails);
        Assert.Equal(2, back.Restarts);
        Assert.Equal(state.UntilAt, back.UntilAt);
        Assert.Equal(state.Moved, back.Moved);
        Assert.Equal(state.Stalled, back.Stalled);
    }

    [Fact]
    public void A_state_file_that_is_missing_or_broken_is_a_fresh_night_rather_than_a_crash()
    {
        // The worst a lost state file costs is a night's bookkeeping. A loop
        // that would not start because of one costs the night.
        Assert.Null(NightState.Read(Path.Combine(_root, "nothing.json")));
        Assert.Null(NightState.Read(""));
        Assert.Null(NightState.Read(null));

        var broken = Path.Combine(_root, "broken.json");
        File.WriteAllText(broken, "{ not json at all");
        Assert.Null(NightState.Read(broken));

        // An empty one, which is what the supervisor's mktemp leaves.
        var empty = Path.Combine(_root, "empty.json");
        File.WriteAllText(empty, "");
        Assert.Null(NightState.Read(empty));
    }

    [Fact]
    public void A_night_with_no_state_path_writes_nothing_and_says_so()
    {
        Assert.False(new NightState().Write(null));
        Assert.False(new NightState().Write(""));

        // And forgetting one that was never there is not an error either.
        NightState.Forget(null);
        NightState.Forget(Path.Combine(_root, "nothing.json"));
    }

    [Fact]
    public void A_carried_night_is_the_tally_it_left_off_at()
    {
        var started = new DateTimeOffset(2026, 9, 7, 22, 0, 0, TimeSpan.Zero);
        var tally = new Tally(TimeProvider.System, new NightState
        {
            Runs = 3,
            Spent = 4.25m,
            Started = started,
            Fails = 2,
            Restarts = 1,
        });

        Assert.Equal(3, tally.Runs);
        Assert.Equal(4.25m, tally.Spent);
        Assert.Equal(2, tally.Fails);
        Assert.Equal(1, tally.Restarts);

        // And handing it on again counts the restart it is handing it on for.
        Assert.Equal(2, tally.ToState().Restarts);
        Assert.Equal(started, tally.ToState().Started);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The operating system's to tidy, not a test failure.
        }
    }
}

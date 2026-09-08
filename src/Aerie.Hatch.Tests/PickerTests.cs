using System.Net;

namespace Aerie.Hatch.Tests;

/// <summary>
/// The walk: the queue picks, the claim decides, and the named read is the
/// payload for the ticket already held.
/// </summary>
public sealed class PickerTests
{
    private const string Queue = "/api/hatch/work/queue";

    private static string Held(string runner) =>
        $"\"aerie-hatch is working this from {runner}, last heard from 8 seconds ago\"";

    [Fact]
    public async Task The_first_clear_row_is_claimed_and_read_back_with_the_token()
    {
        using var h = new Harness();
        var token = Guid.NewGuid();

        h.Wire.Json("GET", Queue, new[] { Fixtures.Row("AER-1", "a question is open"), Fixtures.Row("AER-2") });
        h.Wire.Reply("POST", "/api/hatch/issues/AER-2/claim", HttpStatusCode.OK, Fixtures.Taken(token));
        h.Wire.Json("GET", "/api/hatch/work/AER-2", Fixtures.Work("AER-2"));
        h.Wire.Reply("DELETE", "/api/hatch/issues/AER-2/claim", HttpStatusCode.NoContent);

        var picked = await new Picker(h.Board, "test:/checkout", h.Say)
            .PickAsync(null, 0, default, Harness.Beat);

        Assert.Equal(Pick.Claimed, picked.Outcome);
        Assert.Equal("AER-2", picked.Work!.Issue.Key);

        // The runner's own lease must not fold the runner's own dispatch, which
        // is what the token on the second read is for.
        Assert.Contains($"heldToken={token}", h.Wire.To("GET", "/api/hatch/work/AER-2")[0].Query,
            StringComparison.OrdinalIgnoreCase);

        // The blocked row was never reached for.
        Assert.Empty(h.Wire.To("POST", "/api/hatch/issues/AER-1/claim"));

        await picked.Claim!.ReleaseAsync();
    }

    [Fact]
    public async Task A_ticket_taken_between_the_scan_and_the_claim_moves_the_pass_on()
    {
        using var h = new Harness();
        var token = Guid.NewGuid();

        h.Wire.Json("GET", Queue, new[] { Fixtures.Row("AER-1"), Fixtures.Row("AER-2") });
        h.Wire.Reply("POST", "/api/hatch/issues/AER-1/claim", HttpStatusCode.Conflict, Held("other:/tree"));
        h.Wire.Reply("POST", "/api/hatch/issues/AER-2/claim", HttpStatusCode.OK, Fixtures.Taken(token));
        h.Wire.Json("GET", "/api/hatch/work/AER-2", Fixtures.Work("AER-2"));
        h.Wire.Reply("DELETE", "/api/hatch/issues/AER-2/claim", HttpStatusCode.NoContent);

        var picked = await new Picker(h.Board, "test:/checkout", h.Say)
            .PickAsync(null, 0, default, Harness.Beat);

        // Two loops that started in the same second end up on two tickets, and
        // neither idles because the other got there first.
        Assert.Equal(Pick.Claimed, picked.Outcome);
        Assert.Equal("AER-2", picked.Work!.Issue.Key);
        Assert.Contains(picked.Busy, b => b.Contains("AER-1", StringComparison.Ordinal));

        await picked.Claim!.ReleaseAsync();
    }

    [Fact]
    public async Task A_board_whose_every_candidate_is_taken_is_busy_and_says_who_has_them()
    {
        using var h = new Harness();

        var rows = Enumerable.Range(1, 8).Select(n => Fixtures.Row($"AER-{n}")).ToArray();
        h.Wire.Json("GET", Queue, rows);
        foreach (var row in rows)
            h.Wire.Reply("POST", $"/api/hatch/issues/{row.Issue.Key}/claim", HttpStatusCode.Conflict,
                Held("other:/tree"));

        var picked = await new Picker(h.Board, "test:/checkout", h.Say)
            .PickAsync(null, 0, default, Harness.Beat);

        Assert.Equal(Pick.Busy, picked.Outcome);

        // A bounded walk: a board where the first five are all being worked is a
        // board where waiting an interval is the honest thing to do.
        Assert.Equal(Picker.Attempts, picked.Busy.Count);
        Assert.Equal(Picker.Attempts, h.Wire.Calls.Count(c => c.Path.EndsWith("/claim", StringComparison.Ordinal)));

        var report = Picker.BusyReport(null, picked.Busy).ToList();
        Assert.Contains(report, l => l.Contains("being worked by another runner", StringComparison.Ordinal));
        Assert.Contains(report, l => l.Contains("other:/tree", StringComparison.Ordinal));

        // And it does not read like the empty board, which is the other thing a
        // pass can find and the one that means the night is over.
        Assert.DoesNotContain(report, l => l.Contains("nothing on the board", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_board_with_nothing_clear_is_idle_rather_than_busy()
    {
        using var h = new Harness();
        h.Wire.Json("GET", Queue, new[] { Fixtures.Row("AER-1", "a question is open") });

        var picked = await new Picker(h.Board, "test:/checkout", h.Say)
            .PickAsync(null, 0, default, Harness.Beat);

        Assert.Equal(Pick.Idle, picked.Outcome);
        Assert.Empty(h.Wire.Calls.Where(c => c.Path.EndsWith("/claim", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_ticket_that_changed_under_us_gives_the_lease_straight_back()
    {
        using var h = new Harness();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        h.Wire.Json("GET", Queue, new[] { Fixtures.Row("AER-1"), Fixtures.Row("AER-2") });
        h.Wire.Reply("POST", "/api/hatch/issues/AER-1/claim", HttpStatusCode.OK, Fixtures.Taken(first));
        h.Wire.Json("GET", "/api/hatch/work/AER-1", Fixtures.Work("AER-1", blocked: "a question is open here"));
        h.Wire.Reply("DELETE", "/api/hatch/issues/AER-1/claim", HttpStatusCode.NoContent);
        h.Wire.Reply("POST", "/api/hatch/issues/AER-2/claim", HttpStatusCode.OK, Fixtures.Taken(second));
        h.Wire.Json("GET", "/api/hatch/work/AER-2", Fixtures.Work("AER-2"));
        h.Wire.Reply("DELETE", "/api/hatch/issues/AER-2/claim", HttpStatusCode.NoContent);

        var picked = await new Picker(h.Board, "test:/checkout", h.Say)
            .PickAsync(null, 0, default, Harness.Beat);

        Assert.Equal(Pick.Claimed, picked.Outcome);
        Assert.Equal("AER-2", picked.Work!.Issue.Key);

        // The one it could not use was let go of, not left held until the TTL.
        Assert.Single(h.Wire.To("DELETE", "/api/hatch/issues/AER-1/claim"));

        await picked.Claim!.ReleaseAsync();
    }

    [Fact]
    public async Task A_board_that_will_not_answer_is_neither_idle_nor_busy()
    {
        using var h = new Harness();
        h.Wire.Reply("GET", Queue, HttpStatusCode.InternalServerError, "boom");

        var picked = await new Picker(h.Board, "test:/checkout", h.Say)
            .PickAsync(null, 0, default, Harness.Beat);

        Assert.Equal(Pick.Unreadable, picked.Outcome);
    }

    [Fact]
    public async Task An_epic_narrows_the_scan_and_nothing_else()
    {
        using var h = new Harness();
        h.Wire.Json("GET", Queue, Array.Empty<QueueEntryDto>());

        await new Picker(h.Board, "test:/checkout", h.Say).PickAsync("AER-1", -420, default, Harness.Beat);

        var asked = h.Wire.To("GET", Queue)[0].Query;
        Assert.Contains("ancestorKey=AER-1", asked, StringComparison.Ordinal);
        Assert.Contains("offsetMinutes=-420", asked, StringComparison.Ordinal);
    }
}

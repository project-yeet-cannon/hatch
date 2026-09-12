namespace Hatch.Cli;

/// <summary>
/// The runner's own voice on the board: one call, sent between increments,
/// saying what this process is and reading back what the board would like it to
/// do next.
/// </summary>
/// <remarks>
/// <para>Nothing here can end a night. A heartbeat that does not answer - an
/// origin that is down, a Hatch too old to have the route at all - comes back
/// as no instruction, and a loop with no instruction is a loop running on the
/// flags it was started with, which is exactly what it did before this existed.
/// That is the same judgement <see cref="Claim.BeatAsync"/> makes about
/// weather, and it is why this is not built out of the throwing lane.</para>
///
/// <para>The four bounds go up on every beat and are read on none: the server
/// writes them when it first sees the name and never again, so what a person
/// set at midnight survives the loop's own half-hourly restart. Sending them
/// each time is what makes the row show the truth from its first appearance
/// without the runner having to know whether it is new.</para>
/// </remarks>
public sealed class Runners(HatchClient client, string name)
{
    private readonly string _path = $"/api/hatch/runners/{Uri.EscapeDataString(name)}";

    /// <summary>
    /// Still here. Answers what the board would like, or null where it did not
    /// say - which is not an error and is never worth a line on a terminal.
    /// </summary>
    public async Task<RunnerInstructionDto?> BeatAsync(RunnerHeartbeatRequest beat, CancellationToken ct)
    {
        var answer = await client.Send(HttpMethod.Post, _path, beat, ct);
        if (!answer.Ok || answer.Body.Trim().Length == 0) return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(answer.Body, HatchJson.Default.RunnerInstructionDto);
        }
        catch (System.Text.Json.JsonException)
        {
            // An answer this version cannot read is the same as no answer. A
            // night is not worth losing to a field somebody added.
            return null;
        }
    }
}

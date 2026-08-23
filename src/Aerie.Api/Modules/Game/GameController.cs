using Microsoft.AspNetCore.Mvc;

namespace Aerie.Api.Modules.Game;

/// <summary>
/// The game module's whole API: worlds, the turns that build them, and the two
/// things the running frame reports back (a crash, and a new personal best).
/// </summary>
/// <remarks>
/// Every write goes through IGameService rather than the context, unlike
/// Gather: there is no plain CRUD here. Even renaming a world is the only
/// operation on this controller that does not participate in the version chain,
/// and the rest - turn, repair, revert, undo - are all the same shape underneath
/// and belong together where that shape is enforced.
///
/// A turn is a POST that takes tens of seconds and costs money, so it is
/// deliberately not idempotent and deliberately not cached: two taps mean two
/// turns, which is what the player asked for both times.
/// </remarks>
[ApiController]
[Route("api/game")]
public class GameController(IGameService game, IGameAuthor author) : ControllerBase
{
    /// <summary>Whether this install can write games at all - asked before the text box is shown.</summary>
    [HttpGet("capability")]
    public Task<GameCapabilityDto> GetCapability(CancellationToken ct) => author.GetCapabilityAsync(ct);

    // ---- Worlds ----

    [HttpGet("worlds")]
    public async Task<IReadOnlyList<WorldSummaryDto>> GetWorlds(CancellationToken ct) =>
        await game.GetWorldsAsync(ct);

    /// <summary>Where the app opens: the game last played, or a new one if there has never been a game.</summary>
    [HttpGet("worlds/current")]
    public async Task<WorldDto> GetCurrentWorld(CancellationToken ct) =>
        await game.GetOrCreateCurrentWorldAsync(ct);

    [HttpGet("worlds/{id:guid}")]
    public async Task<ActionResult<WorldDto>> GetWorld(Guid id, CancellationToken ct) =>
        await game.GetWorldAsync(id, ct) is { } world ? world : NotFound();

    [HttpPost("worlds")]
    public async Task<ActionResult<WorldDto>> CreateWorld(WorldWriteRequest request, CancellationToken ct) =>
        await game.CreateWorldAsync(request, ct);

    [HttpPut("worlds/{id:guid}")]
    public async Task<ActionResult<WorldSummaryDto>> RenameWorld(Guid id, WorldWriteRequest request, CancellationToken ct) =>
        await game.RenameWorldAsync(id, request, ct) is { } world ? world : NotFound();

    /// <summary>Takes the world's whole history with it - the FK cascades.</summary>
    [HttpDelete("worlds/{id:guid}")]
    public async Task<IActionResult> DeleteWorld(Guid id, CancellationToken ct) =>
        await game.DeleteWorldAsync(id, ct) ? NoContent() : NotFound();

    // ---- History ----

    [HttpGet("worlds/{id:guid}/versions")]
    public async Task<IReadOnlyList<VersionDto>> GetVersions(Guid id, CancellationToken ct) =>
        await game.GetVersionsAsync(id, ct);

    // ---- Turns ----

    /// <summary>
    /// The main event: someone typed something, so the game changes. Slow by
    /// nature - tens of seconds - and the response is the world as it now
    /// stands, so the client can swap code into the frame without a second read.
    /// </summary>
    [HttpPost("worlds/{id:guid}/turns")]
    public async Task<ActionResult<WorldDto>> TakeTurn(Guid id, TurnRequest request, CancellationToken ct)
    {
        try
        {
            return await game.TakeTurnAsync(id, request, ct) is { } world ? world : NotFound();
        }
        catch (GameAuthorException ex)
        {
            return Problem(ex);
        }
    }

    /// <summary>
    /// The frame reporting that the current version threw, and asking for a fix
    /// in the same breath. Answers with the world either way: when the report is
    /// stale the answer is simply the world unchanged, which is what the caller
    /// needs to know.
    /// </summary>
    [HttpPost("worlds/{id:guid}/repair")]
    public async Task<ActionResult<WorldDto>> Repair(Guid id, BreakageReport report, CancellationToken ct)
    {
        try
        {
            return await game.RepairAsync(id, report, ct) is { } world ? world : NotFound();
        }
        catch (GameAuthorException ex)
        {
            return Problem(ex);
        }
    }

    /// <summary>
    /// Marks a version broken without asking for a fix - what the client sends
    /// when it has given up repairing and is about to fall back to a version
    /// that worked.
    /// </summary>
    [HttpPost("worlds/{id:guid}/breakage")]
    public async Task<IActionResult> ReportBreakage(Guid id, BreakageReport report, CancellationToken ct) =>
        await game.ReportBreakageAsync(id, report, ct) ? NoContent() : NotFound();

    /// <summary>One tap back, no arguments - see IGameService.UndoAsync.</summary>
    [HttpPost("worlds/{id:guid}/undo")]
    public async Task<ActionResult<WorldDto>> Undo(Guid id, CancellationToken ct)
    {
        try
        {
            return await game.UndoAsync(id, ct) is { } world ? world : NotFound();
        }
        catch (GameAuthorException ex)
        {
            return Problem(ex);
        }
    }

    /// <summary>Back to one particular version, chosen from the history screen.</summary>
    [HttpPost("worlds/{id:guid}/revert")]
    public async Task<ActionResult<WorldDto>> Revert(Guid id, RevertRequest request, CancellationToken ct)
    {
        try
        {
            return await game.RevertAsync(id, request.VersionId, ct) is { } world ? world : NotFound();
        }
        catch (GameAuthorException ex)
        {
            return Problem(ex);
        }
    }

    // ---- Records ----

    /// <summary>
    /// A personal best, sent up by the frame that just watched it happen. The
    /// server stores the list whole rather than merging: the engine is the only
    /// thing that knows what beating a record means, and two tabs racing over
    /// whose longest jump is longer is not a problem worth a merge strategy.
    /// </summary>
    [HttpPut("worlds/{id:guid}/records")]
    public async Task<IActionResult> SaveRecords(Guid id, RecordsWriteRequest request, CancellationToken ct) =>
        await game.SaveRecordsAsync(id, request, ct) ? NoContent() : NotFound();

    /// <summary>
    /// A missing API key is the install's fault and fixable; everything else
    /// here is the model or the network, and a retry is the honest advice. The
    /// message is written to be shown to whoever is holding the tablet.
    /// </summary>
    private ObjectResult Problem(GameAuthorException ex) =>
        StatusCode(ex.IsConfiguration ? StatusCodes.Status409Conflict : StatusCodes.Status502BadGateway, ex.Message);
}

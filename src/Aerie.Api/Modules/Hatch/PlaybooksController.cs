using Aerie.Api.Common;
using Aerie.Api.Ef;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// The matrix an agent is dispatched by: for a transition and a set of issue
/// types, what to say, which model to say it to, and how hard to think.
///
/// The split down the middle of this controller is the whole design. Reading is
/// open to an API key, because an agent has to be told what it was dispatched
/// with. Writing is not: <see cref="RequireAdminAttribute"/> with no scope
/// named refuses a key outright, so the rows that choose an agent's
/// instructions, its model and its budget can only be changed by a person at a
/// browser.
/// </summary>
/// <remarks>
/// That refusal is not a guess about what an agent would do with the write. It
/// is the one edge in this graph that closes a loop: an agent that can widen
/// its own prompt and raise its own effort has no fixed point to settle at, and
/// the failure is unbounded spend rather than a wrong answer. Cutting the edge
/// costs nothing - nobody wants a tracker that retunes itself unwatched - and
/// it is cut here rather than asked for politely in a prompt.
/// </remarks>
[ApiController]
[Route("api/hatch/playbooks")]
public class PlaybooksController(HatchContext db, TimeProvider time) : ControllerBase
{
    /// <summary>Every row, in board order - the order the page draws them in.</summary>
    [HttpGet]
    [RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
    public async Task<ActionResult<IReadOnlyList<PlaybookDto>>> GetPlaybooks(CancellationToken ct)
    {
        var rows = await db.Playbooks.AsNoTracking()
            .Include(p => p.FromStatus)
            .Include(p => p.ToStatus)
            .ToListAsync(ct);

        return rows
            .OrderBy(p => p.FromStatus!.SortOrder)
            .ThenBy(p => p.ToStatus!.SortOrder)
            .ThenBy(p => p.Types, StringComparer.Ordinal)
            .Select(ToDto)
            .ToList();
    }

    [HttpPost]
    [RequireAdmin]
    public async Task<ActionResult<PlaybookDto>> CreatePlaybook(PlaybookCreateRequest request, CancellationToken ct)
    {
        var types = EfHatchPlaybook.NormalizeTypes(request.Types);
        if (await Refusal(request.FromStatusId, request.ToStatusId, types, id: null, ct) is { } error)
            return BadRequest(error);

        var prompt = request.Prompt?.Trim() ?? "";
        if (InvalidPrompt(prompt) is { } promptError) return BadRequest(promptError);

        var model = request.Model?.Trim() ?? EfHatchPlaybook.DefaultModel;
        var effort = request.Effort?.Trim() ?? EfHatchPlaybook.DefaultEffort;
        if (InvalidDispatch(model, effort) is { } dispatchError) return BadRequest(dispatchError);

        var now = time.GetUtcNow();
        var playbook = new EfHatchPlaybook
        {
            FromStatusId = request.FromStatusId,
            ToStatusId = request.ToStatusId,
            Types = types,
            Prompt = prompt,
            Model = model,
            Effort = effort,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Playbooks.Add(playbook);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetPlaybooks), await LoadDtoAsync(playbook.Id, ct));
    }

    [HttpPatch("{id:int}")]
    [RequireAdmin]
    public async Task<ActionResult<PlaybookDto>> PatchPlaybook(int id, PlaybookPatchRequest request, CancellationToken ct)
    {
        var playbook = await db.Playbooks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (playbook is null) return NotFound();

        var from = request.FromStatusId ?? playbook.FromStatusId;
        var to = request.ToStatusId ?? playbook.ToStatusId;
        var types = request.Types is null ? playbook.Types : EfHatchPlaybook.NormalizeTypes(request.Types);

        if (await Refusal(from, to, types, id, ct) is { } error) return BadRequest(error);

        if (request.Prompt is not null)
        {
            var prompt = request.Prompt.Trim();
            if (InvalidPrompt(prompt) is { } promptError) return BadRequest(promptError);
            playbook.Prompt = prompt;
        }

        var model = request.Model?.Trim() ?? playbook.Model;
        var effort = request.Effort?.Trim() ?? playbook.Effort;
        if (InvalidDispatch(model, effort) is { } dispatchError) return BadRequest(dispatchError);

        playbook.FromStatusId = from;
        playbook.ToStatusId = to;
        playbook.Types = types;
        playbook.Model = model;
        playbook.Effort = effort;
        playbook.UpdatedAt = time.GetUtcNow();

        await db.SaveChangesAsync(ct);
        return await LoadDtoAsync(id, ct);
    }

    [HttpDelete("{id:int}")]
    [RequireAdmin]
    public async Task<IActionResult> DeletePlaybook(int id, CancellationToken ct)
    {
        var playbook = await db.Playbooks.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (playbook is null) return NotFound();

        db.Playbooks.Remove(playbook);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---- Refusals ----

    /// <summary>
    /// Everything about a row that is wrong before its prompt is even read: a
    /// column that does not exist, a transition that goes nowhere, or a
    /// duplicate of one already filed.
    /// </summary>
    private async Task<string?> Refusal(int from, int to, string types, int? id, CancellationToken ct)
    {
        if (from == to) return "a playbook moves an issue between two columns, not into the one it is in";

        var known = await db.Statuses.Where(s => s.Id == from || s.Id == to).Select(s => s.Id).ToListAsync(ct);
        if (!known.Contains(from)) return $"there is no column {from}";
        if (!known.Contains(to)) return $"there is no column {to}";

        var clash = await db.Playbooks
            .AnyAsync(p => p.FromStatusId == from && p.ToStatusId == to && p.Types == types && p.Id != id, ct);

        return clash
            ? "there is already a playbook for that transition and those types"
            : null;
    }

    private static string? InvalidPrompt(string prompt) => prompt switch
    {
        "" => "a playbook needs a prompt - it is the whole instruction the agent gets",
        { Length: > EfHatchPlaybook.MaxPromptLength } =>
            $"a prompt is at most {EfHatchPlaybook.MaxPromptLength} characters",
        _ => null,
    };

    private static string? InvalidDispatch(string model, string effort) =>
        InvalidModel(model) ?? InvalidEffort(effort);

    /// <summary>
    /// Why this is not a model, in the sentence the operator reads. Shared
    /// with <see cref="IssuePlaybookController"/> rather than written twice:
    /// an issue's override holds a playbook's value, and two copies of a
    /// refusal are two things to keep in step. Per field, so a request naming
    /// only an effort is not judged on a model it never sent.
    /// </summary>
    internal static string? InvalidModel(string model) =>
        EfHatchPlaybook.IsValidModel(model)
            ? null
            : $"a model is one of {string.Join(", ", EfHatchPlaybook.ModelAliases)}, " +
              $"or a pinned name like claude-opus-5 - not \"{model}\"";

    /// <summary>The same, for the thinking budget.</summary>
    internal static string? InvalidEffort(string effort) =>
        EfHatchPlaybook.IsValidEffort(effort)
            ? null
            : $"an effort is one of {string.Join(", ", EfHatchPlaybook.Efforts)} - not \"{effort}\"";

    // ---- Reading back ----

    private async Task<PlaybookDto> LoadDtoAsync(int id, CancellationToken ct)
    {
        var row = await db.Playbooks.AsNoTracking()
            .Include(p => p.FromStatus)
            .Include(p => p.ToStatus)
            .FirstAsync(p => p.Id == id, ct);

        return ToDto(row);
    }

    internal static PlaybookDto ToDto(EfHatchPlaybook p) => new(
        p.Id,
        p.FromStatusId,
        p.FromStatus?.Name ?? "",
        p.ToStatusId,
        p.ToStatus?.Name ?? "",
        EfHatchPlaybook.SplitTypes(p.Types),
        p.Prompt,
        p.Model,
        p.Effort,
        p.UpdatedAt);
}

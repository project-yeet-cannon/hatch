using Hatch.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Modules.Quill;

/// <summary>
/// Quill's whole API: one person's notes.
///
/// Every endpoint begins the same way, and that repetition is the design. The
/// caller's person id is resolved once (<see cref="ICallerIdentity"/>) and is
/// the first clause of every query - so there is no "read a note" path that
/// could be reached without it, and no admin escape hatch beside it that would
/// have to be defended separately.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every refusal is a 404.</b> No person on this device is a 404, someone
/// else's note id is a 404, and a note that does not exist is a 404 - the same
/// empty body in all three cases. A 403 would confirm that the id names a real
/// note belonging to a real person, which is the one fact a private note has to
/// keep; and "this device has no owner" is not something a device with no owner
/// should learn from the API when the shell has already declined to show it the
/// app at all.
/// </para>
/// <para>
/// This is the first controller in Hatch where a person decides an outcome, and
/// it will not be the last - sharing a note with somebody, read or write, is
/// per-resource authorization keyed on a person and nothing else
/// (docs/quill.md). What does not exist yet is a permission model to say that
/// in, so the shape here is the one worth copying rather than improvising on:
/// the person is a *clause in the query* (see LoadAsync), never a check after
/// the rows are loaded, and "who may read this" is one expression that sharing
/// widens rather than a second one beside it.
///
/// What that deliberately is not: nothing here reads Person.IsAdmin. There is
/// a global role now - it guards the admin app and the operator verbs behind
/// it (docs/auth-architecture.md, "The admin flag") - and it has nothing to say
/// about a note. An administrator is not a person who may read everyone's
/// notes, and the day somebody wants that, it is a sharing rule rather than a
/// role check.
/// </para>
/// </remarks>
[ApiController]
[Route("api/quill")]
public class QuillController(QuillContext db, ICallerIdentity caller, TimeProvider time) : ControllerBase
{
    /// <summary>
    /// Every note this person has, most recently edited first - the list
    /// screen, and the whole of what the shell mirrors for offline reading.
    /// </summary>
    [HttpGet("notes")]
    public async Task<ActionResult<IReadOnlyList<NoteDto>>> GetNotes(CancellationToken ct)
    {
        if (await PersonAsync(ct) is not { } personId) return NotFound();

        var notes = await db.Notes.AsNoTracking()
            .Where(n => n.PersonId == personId)
            .OrderByDescending(n => n.UpdatedAt)
            // Ids break the tie, so two notes saved in the same tick do not
            // trade places between polls under someone's thumb.
            .ThenBy(n => n.Id)
            .ToListAsync(ct);

        return notes.Select(ToDto).ToList();
    }

    /// <summary>
    /// One note. The list already carries it, so this exists for the case the
    /// list cannot serve: a link opened cold into a note that was written on
    /// another device since this one last synced.
    /// </summary>
    [HttpGet("notes/{id:guid}")]
    public async Task<ActionResult<NoteDto>> GetNote(Guid id, CancellationToken ct)
    {
        var note = await LoadAsync(id, ct);
        return note is null ? NotFound() : ToDto(note);
    }

    [HttpPost("notes")]
    public async Task<ActionResult<NoteDto>> CreateNote(NoteWriteRequest request, CancellationToken ct)
    {
        if (await PersonAsync(ct) is not { } personId) return NotFound();
        if (Invalid(request) is { } error) return BadRequest(error);

        // A note with nothing in it is not a note. The editor opens on a blank
        // body and saves itself as you type, so without this every time someone
        // taps New and changes their mind would leave a row behind.
        if (QuillNote.IsBlank(request.Title, request.Body)) return BadRequest("a note needs something in it");

        var now = time.GetUtcNow();
        var note = new QuillNote
        {
            PersonId = personId,
            Title = Clean(request.Title),
            Body = request.Body ?? "",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetNote), new { id = note.Id }, ToDto(note));
    }

    /// <summary>
    /// Overwrites the note, both fields. Blanking both is allowed and leaves an
    /// empty note rather than deleting it: the editor saves continuously, and a
    /// server that deletes a row the moment someone selects-all and starts
    /// retyping is a server that eats notes. Deleting is its own verb.
    /// </summary>
    [HttpPut("notes/{id:guid}")]
    public async Task<ActionResult<NoteDto>> UpdateNote(Guid id, NoteWriteRequest request, CancellationToken ct)
    {
        if (Invalid(request) is { } error) return BadRequest(error);

        var note = await LoadAsync(id, ct);
        if (note is null) return NotFound();

        note.Title = Clean(request.Title);
        note.Body = request.Body ?? "";
        note.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return ToDto(note);
    }

    [HttpDelete("notes/{id:guid}")]
    public async Task<IActionResult> DeleteNote(Guid id, CancellationToken ct)
    {
        var note = await LoadAsync(id, ct);
        if (note is null) return NotFound();

        db.Notes.Remove(note);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// The caller's person, or null for a device nobody has claimed - and for a
    /// caller with no grant at all, which is every request while the wall is
    /// off. One null, one refusal; see the class remarks.
    /// </summary>
    private Task<Guid?> PersonAsync(CancellationToken ct) => caller.PersonIdAsync(ct);

    /// <summary>
    /// A note of the caller's, by id. Ownership is a clause in the query rather
    /// than a check after the load, so there is no moment where the wrong
    /// person's note is in a variable - and a mistake here is a 404 rather than
    /// a leak, because the row simply is not selected.
    /// </summary>
    private async Task<QuillNote?> LoadAsync(Guid id, CancellationToken ct)
    {
        if (await PersonAsync(ct) is not { } personId) return null;

        return await db.Notes.FirstOrDefaultAsync(n => n.Id == id && n.PersonId == personId, ct);
    }

    /// <summary>
    /// Lengths, counted on what a person typed. The columns are unbounded text
    /// holding the protected form, so nothing below this would catch an
    /// over-long note - it would simply store it.
    /// </summary>
    private static string? Invalid(NoteWriteRequest request)
    {
        if (request.Title?.Trim().Length > QuillNote.MaxTitleLength)
            return $"title must be at most {QuillNote.MaxTitleLength} characters";
        if (request.Body?.Length > QuillNote.MaxBodyLength)
            return $"the note must be at most {QuillNote.MaxBodyLength} characters";
        return null;
    }

    /// <summary>
    /// The title, trimmed. Never the body: leading blank lines and trailing
    /// whitespace are things a person typed into their own note, and a server
    /// tidying them up moves the cursor out from under them on the next sync.
    /// </summary>
    private static string Clean(string? title) => title?.Trim() ?? "";

    private static NoteDto ToDto(QuillNote n) => new(n.Id, n.Title, n.Body, n.CreatedAt, n.UpdatedAt);
}

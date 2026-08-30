using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Models.People;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Controllers;

/// <summary>
/// CRUD for the household's people, plus the two endpoints that move a photo in
/// and out.
///
/// Written against <see cref="AerieContext"/> directly rather than through a
/// service, matching <see cref="ZonesController"/>: this is admin CRUD over one
/// table, and the only logic worth a seam - what a name may contain, what an
/// upload may be - already has one in <see cref="PersonName"/> and
/// <see cref="PersonPhoto"/>, where it can be tested without an HTTP request in
/// the picture.
///
/// The link between a person and a session is *not* here. It is a column on the
/// grant, so it is written where grants are written
/// (<c>AuthController.LinkGrantPerson</c>) - one FK with one write path. What
/// this controller offers is the read of it, on <see cref="GetSessions"/>.
/// </summary>
[ApiController]
[Route("api/people")]
public class PeopleController(AerieContext db, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<PersonDto>> GetAll(CancellationToken ct)
        => await db.People.AsNoTracking()
            // By name, because the People page is a list someone reads rather
            // than an ordering someone chose - there is no sort order column
            // here on purpose, and creation order is meaningless to a reader.
            .OrderBy(p => p.Name)
            .Select(ToDto)
            .ToListAsync(ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PersonDto>> Get(Guid id, CancellationToken ct)
    {
        var person = await db.People.AsNoTracking().Where(p => p.Id == id).Select(ToDto).FirstOrDefaultAsync(ct);
        return person is null ? NotFound() : person;
    }

    [HttpPost]
    public async Task<ActionResult<PersonDto>> Create(PersonWriteRequest request, CancellationToken ct)
    {
        if (!PersonName.TryNormalize(request.Name, out var name, out var error)) return BadRequest(error);

        var now = time.GetUtcNow();
        var person = new EfPerson { Name = name, CreatedAt = now, UpdatedAt = now };
        db.People.Add(person);
        await db.SaveChangesAsync(ct);

        // Two people may share a name and that is not an error - households
        // contain a Sam and a Sam, and the id is what anything actually keys
        // on. There is deliberately no unique index behind this.
        return CreatedAtAction(nameof(Get), new { id = person.Id }, new PersonDto(person.Id, person.Name, person.CreatedAt, person.UpdatedAt, null, 0));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PersonDto>> Update(Guid id, PersonWriteRequest request, CancellationToken ct)
    {
        if (!PersonName.TryNormalize(request.Name, out var name, out var error)) return BadRequest(error);

        var person = await db.People.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (person is null) return NotFound();

        person.Name = name;
        person.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return await Get(id, ct);
    }

    /// <summary>
    /// Deletes the person. Their photo goes with them (cascade), and their
    /// sessions do not: those are enrolled devices that still work and whose
    /// owner is now simply unknown. Deleting a person must never be a way to
    /// lock a tablet out of the house.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var person = await db.People.FindAsync([id], ct);
        if (person is null) return NotFound();

        db.People.Remove(person);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---- Sessions (read-only here; the write lives on AuthController) ----

    /// <summary>Which enrolled devices are this person's. Empty is the normal answer for someone who has never held a tablet.</summary>
    [HttpGet("{id:guid}/sessions")]
    public async Task<ActionResult<IReadOnlyList<PersonSessionDto>>> GetSessions(Guid id, CancellationToken ct)
    {
        if (!await db.People.AnyAsync(p => p.Id == id, ct)) return NotFound();

        return await db.AuthGrants.AsNoTracking()
            .Where(g => g.PersonId == id)
            .OrderByDescending(g => g.CreatedAt)
            .Select(g => new PersonSessionDto(g.Id, g.Label, g.Kind, g.CreatedAt, g.LastSeenAt))
            .ToListAsync(ct);
    }

    // ---- Photo ----

    /// <summary>
    /// The bytes, with the type they were sniffed as at upload. Cached
    /// privately and revalidated by ETag rather than cached hard: the URL is
    /// stable across uploads on purpose (so an &lt;img src&gt; never has to be
    /// rebuilt), which means the freshness has to come from the validator.
    /// </summary>
    [HttpGet("{id:guid}/photo")]
    public async Task<IActionResult> GetPhoto(Guid id, CancellationToken ct)
    {
        var photo = await db.PersonPhotos.AsNoTracking().FirstOrDefaultAsync(p => p.PersonId == id, ct);
        if (photo is null) return NotFound();

        // nosniff, because this is user-supplied content served from the
        // install's own origin. The type was sniffed rather than taken on
        // trust (PersonPhoto), and this is the other half of that: the browser
        // is told not to second-guess it either.
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "private, max-age=0, must-revalidate";

        // The upload time *is* the version - a photo cannot change without it
        // changing - so it makes the ETag directly rather than hashing two
        // megabytes on every request to learn something already in the row.
        var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{photo.UpdatedAt.UtcTicks:x}\"");
        return File(photo.Bytes, photo.ContentType, lastModified: photo.UpdatedAt, entityTag: etag);
    }

    /// <summary>
    /// Replaces the photo. The body is the image itself rather than a multipart
    /// form: there is exactly one file and no fields beside it, so a multipart
    /// envelope would be ceremony around a single blob - and the browser sends
    /// a Blob as a raw body without any help.
    ///
    /// The request's own Content-Type is ignored entirely; see
    /// <see cref="PersonPhoto"/>.
    /// </summary>
    [HttpPut("{id:guid}/photo")]
    [RequestSizeLimit(PersonPhoto.MaxBytes + 1024)]
    public async Task<ActionResult<PersonDto>> PutPhoto(Guid id, CancellationToken ct)
    {
        var person = await db.People.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (person is null) return NotFound();

        // Bounded read. Buffering an unbounded request body into memory to find
        // out how big it is, is the bug this cap exists to prevent - so the
        // cap is applied to the read itself, not to the result of it.
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        if (!PersonPhoto.TryDetectContentType(bytes, out var contentType, out var error)) return BadRequest(error);

        var now = time.GetUtcNow();
        var photo = await db.PersonPhotos.FirstOrDefaultAsync(p => p.PersonId == id, ct);
        if (photo is null)
        {
            db.PersonPhotos.Add(new EfPersonPhoto { PersonId = id, Bytes = bytes, ContentType = contentType, UpdatedAt = now });
        }
        else
        {
            photo.Bytes = bytes;
            photo.ContentType = contentType;
            photo.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    [HttpDelete("{id:guid}/photo")]
    public async Task<IActionResult> DeletePhoto(Guid id, CancellationToken ct)
    {
        var photo = await db.PersonPhotos.FirstOrDefaultAsync(p => p.PersonId == id, ct);
        if (photo is null) return NotFound();

        db.PersonPhotos.Remove(photo);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// One projection, shared by every read, so the list and the single-item
    /// reads cannot drift. An expression rather than a method because it has to
    /// translate to SQL - the photo's presence is a join, not a load, and the
    /// session count is a subquery rather than a second round trip.
    /// </summary>
    private static readonly System.Linq.Expressions.Expression<Func<EfPerson, PersonDto>> ToDto =
        p => new PersonDto(
            p.Id,
            p.Name,
            p.CreatedAt,
            p.UpdatedAt,
            p.Photo == null ? null : p.Photo.UpdatedAt,
            p.Grants.Count);
}

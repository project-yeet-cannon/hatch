using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Quill;

/// <summary>
/// One note, belonging to exactly one person.
///
/// The ownership is the entity's whole shape. Every other module's rows belong
/// to the household - a crate is in the garage whoever opens the app - and a
/// note is the first row in Aerie that belongs to a person instead
/// (docs/quill.md). <see cref="PersonId"/> is therefore not a stamp saying who
/// wrote it: it is the only thing that decides who may read it, and every query
/// in the module is filtered by it.
/// </summary>
/// <remarks>
/// Sharing is deliberately absent rather than deferred badly. It would be a
/// join table beside this row, not a second column here, so nothing about this
/// shape has to change when it arrives - and until it does, "who can read this"
/// has exactly one answer, which is the property worth having while the
/// protection underneath is obfuscation rather than encryption.
/// </remarks>
[Table("Notes")]
// The one query the module runs: this person's notes, most recently touched
// first. Composite and in that order so it answers both the filter and the
// sort - a PersonId-only index would leave a sort in front of every read.
[Index(nameof(PersonId), nameof(UpdatedAt))]
public class QuillNote
{
    /// <summary>
    /// A heading, not a subject line. Short because it is read in a list on a
    /// phone, and an untitled note is the ordinary case rather than an error -
    /// see <see cref="Title"/>.
    /// </summary>
    public const int MaxTitleLength = 120;

    /// <summary>
    /// Long enough that nobody writing prose on a phone will ever meet it, and
    /// short enough that a single request cannot be used to fill the database.
    /// The column itself is unbounded <c>text</c>; this is the API's cap, which
    /// is what turns an over-long body into a 400 that says so rather than a
    /// truncation nobody notices.
    /// </summary>
    public const int MaxBodyLength = 100_000;

    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>
    /// Whose note this is. Not a foreign key, matching AuthInvites.PersonId and
    /// for a related reason: People lives in the core <c>public</c> schema and
    /// this table lives in <c>quill</c>, and a cross-schema FK from a module
    /// into the platform is the compile-time coupling Modules/README.md exists
    /// to prevent. A deleted person's notes become unreadable rather than
    /// cascading, which is the safer failure - see docs/quill.md.
    /// </summary>
    public required Guid PersonId { get; set; }

    /// <summary>
    /// Blank for an untitled note, never null. An empty string is a real state
    /// here - you start typing the body and may never name the thing - so the
    /// list renders "Untitled" plus the start of the body, and nothing in the
    /// write path treats an untitled note as incomplete.
    /// </summary>
    /// <remarks>
    /// Protected at rest by a value converter on the context, like
    /// <see cref="Body"/>. A title is often the most revealing line in the file.
    ///
    /// Unbounded <c>text</c>, with no <c>MaxLength</c>, for the same reason the
    /// body has none: the column holds the base64 of the XOR'd bytes - about
    /// 4/3 the length of what was typed, more for anything outside ASCII - so a
    /// column bound would be a bound on the wrong string. The API counts
    /// <see cref="MaxTitleLength"/> characters of what a person actually typed.
    /// </remarks>
    public required string Title { get; set; }

    /// <summary>
    /// The note. Protected at rest (QuillContext.OnModelCreating) - so this
    /// property is plaintext everywhere in the process and ciphertext everywhere
    /// in Postgres, with no call site able to get that wrong.
    /// </summary>
    public required string Body { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Last edit, and the list's sort key. Written on every save including one
    /// that only changed the title, because "what was I working on" is the
    /// question a notes list answers.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Whether a note has nothing in it at all - what the API refuses to create.</summary>
    public static bool IsBlank(string? title, string? body) =>
        string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body);
}

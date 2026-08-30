using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Aerie.Api.Ef;

/// <summary>
/// One human the household knows about - primarily but not necessarily a family
/// member. This is the row <see cref="EfAuthGrant"/> spent its first release
/// deliberately not pointing at ("grants become people when people exist"), and
/// the answer to the rule in Modules/README.md that no module may invent a user:
/// people live in the core <c>public</c> schema precisely so that every module
/// can read one without depending on another module.
///
/// A person is not an account. There is nothing to sign in as, no password, no
/// scope - the wall still authenticates a *device* (docs/auth-architecture.md).
/// A person is the name you hang on one, so that a log line, a session list, or
/// a future permission has a human to point at.
///
/// Deliberately two columns wide. Everything a person will eventually carry -
/// a birthday, a colour, a pronoun, a phone - is additive against this, and
/// guessing at those now would mean guessing wrong in a table that other
/// modules are about to depend on.
/// </summary>
[Table("People")]
public class EfPerson
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>
    /// What this person is called, already normalized by
    /// <see cref="Aerie.Api.Common.PersonName"/> - never raw request text. The
    /// column is sized in UTF-16 chars and the rule is written in grapheme
    /// clusters, which is why the two numbers differ by a factor of four; see
    /// PersonName for why a name that is 60 things to a reader can be 240 to a
    /// database.
    /// </summary>
    [MaxLength(Common.PersonName.MaxChars)]
    public required string Name { get; set; }

    /// <summary>
    /// Whether this person is an administrator. **Nothing enforces it.** Not
    /// the wall, not a controller, not a filter - the column is written by the
    /// admin app and read by the admin app, and that is the whole of it today.
    ///
    /// It is here early on purpose. The wall authenticates a device and every
    /// enrolled device can currently do everything, which is the deliberate
    /// gate-not-permissions call in docs/auth-architecture.md. When that
    /// changes, the first question any design has to answer is "which of these
    /// people is allowed", and a column that has been carried and edited for a
    /// while has real answers in it - whereas a column added on the day
    /// enforcement lands starts empty, which means the deploy that turns
    /// enforcement on is also the deploy that locks everyone out.
    ///
    /// So: a bool now, an input on the People page, and no branch anywhere.
    /// Whatever this eventually becomes - roles, scopes, RBAC - inherits a
    /// populated column rather than an empty one. Do not start reading it for
    /// authorization without designing the lockout path first; there is exactly
    /// one household member holding the bootstrap invite.
    /// </summary>
    public bool IsAdmin { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Last write to the name. Not an audit trail - there is no actor here -
    /// but it is what a "recently changed" sort would use, and it costs a
    /// column now versus a migration later.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The photo, in its own table so that it is only loaded when it is asked
    /// for. Null means nobody has uploaded one, which is the state every person
    /// starts in and a perfectly good one to stay in.
    /// </summary>
    public EfPersonPhoto? Photo { get; set; }

    /// <summary>
    /// Every enrolled device linked to this person - zero to many, and zero is
    /// ordinary. A person with no sessions is someone the household knows about
    /// who has never held a tablet, which is most of the reason a person is not
    /// an account.
    ///
    /// The inverse of a nullable FK on the grant, so deleting a person empties
    /// this list rather than deleting what is in it; see EfAuthGrant.PersonId.
    /// </summary>
    public ICollection<EfAuthGrant> Grants { get; set; } = [];
}

/// <summary>
/// One person's photo, stored as bytes in Postgres rather than as a path into a
/// volume.
///
/// The size argument that usually rules this out does not apply: these are
/// avatars, capped at <see cref="Aerie.Api.Common.PersonPhoto.MaxBytes"/>, one
/// per person, in a household. What does apply is that a row and a file on a
/// PVC can disagree - a restored database pointing at photos that are not
/// there is a failure mode with no obvious symptom - whereas bytes in the row
/// ride the existing CNPG backup and the disaster-recovery path unchanged
/// (docs/disaster-recovery.md). No new volume, no new backup story.
///
/// Its own table, keyed by PersonId, so that listing people never drags the
/// blobs along: EF has no way to project a column out of an entity that is
/// always loaded, and the People page reads every row.
/// </summary>
[Table("PersonPhotos")]
public class EfPersonPhoto
{
    /// <summary>Both the primary key and the foreign key - one photo per person, enforced by the schema rather than by the code that writes it.</summary>
    [Key]
    public Guid PersonId { get; set; }

    public EfPerson? Person { get; set; }

    /// <summary>The image exactly as it was accepted. Not re-encoded: what was validated is what is served, so there is no second format to reason about.</summary>
    public required byte[] Bytes { get; set; }

    /// <summary>
    /// Sniffed from the bytes by <see cref="Aerie.Api.Common.PersonPhoto"/>,
    /// never taken from the request's Content-Type. A client that says PNG and
    /// sends HTML is describing an attack, not a picture.
    /// </summary>
    [MaxLength(64)]
    public required string ContentType { get; set; }

    /// <summary>
    /// When these bytes were stored. Serves as the photo's ETag, which is what
    /// lets the admin app point an &lt;img&gt; at a stable URL and still see a
    /// new upload immediately.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; set; }
}

namespace Aerie.Api.Models.People;

/// <summary>
/// One person, as the admin People page sees them.
///
/// The photo is not here and will not be: it is bytes, it is served from its
/// own URL, and inlining even a base64 avatar into a list response makes the
/// list response the size of the photos. <paramref name="PhotoUpdatedAt"/> is
/// the substitute - it says both *whether* there is a photo and *which* photo,
/// which is what lets the client point an &lt;img&gt; at a stable URL and still
/// see a new upload the moment it lands.
/// </summary>
/// <param name="SessionCount">
/// How many enrolled devices are linked to this person. Included because it is
/// the answer to the only question the People list raises on its own - "can
/// this person actually get in?" - and computing it here costs one GROUP BY
/// against a table with a household's worth of rows in it.
/// </param>
public record PersonDto(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PhotoUpdatedAt,
    int SessionCount)
{
    public bool HasPhoto => PhotoUpdatedAt is not null;
}

/// <summary>
/// What the People page posts. One field, deliberately: everything else a
/// person will grow is additive, and a write model with room for fields that do
/// not exist yet is a write model nobody can read.
///
/// <paramref name="Name"/> is raw text - normalization is the server's job
/// (<see cref="Aerie.Api.Common.PersonName"/>), because a client that
/// normalizes is a client that can be replaced by one that does not.
/// </summary>
public record PersonWriteRequest(string? Name);

/// <summary>
/// One enrolled device on a person's row. A deliberately thinner view than
/// <c>AuthGrantDto</c>: the People page is answering "which devices are hers",
/// not offering to revoke them, and the fields that only make sense next to a
/// Revoke button belong on the page that has one.
/// </summary>
public record PersonSessionDto(
    Guid Id,
    string Label,
    Ef.AuthGrantKind Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt);

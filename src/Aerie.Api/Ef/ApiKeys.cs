using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Ef;

/// <summary>
/// A credential for something that is not a browser - today, Claude working an
/// issue in Hatch through <c>Authorization: Bearer</c>.
///
/// It is a peer of <see cref="EfAuthGrant"/> rather than a variant of one. A
/// grant is a device the household enrolled and the wall lets in whole; a key
/// is a named program allowed a stated slice, and the two differ in every field
/// that matters - a key carries scopes and a grant does not, a key is revoked
/// by a column and a grant by a DELETE, a key is never re-issued into a cookie.
/// Folding them into one table would mean a nullable half of the row for each.
/// </summary>
/// <remarks>
/// In the core <c>public</c> schema, deliberately, and not in <c>hatch</c>
/// beside the app that prompted it. The wall reads this table on the way in, so
/// a module owning it would make the wall depend on a module - the one thing
/// Modules/README.md says never happens. The scope strings name modules; the
/// rows do not belong to them.
///
/// Only the SHA-256 of the secret is stored, exactly as for a grant token: a
/// stolen database yields nothing usable, and a lost key is replaced by minting
/// another rather than by looking this one up.
/// </remarks>
[Table("ApiKeys")]
[Index(nameof(Hash), IsUnique = true)]
[Index(nameof(Name), IsUnique = true)]
public class EfApiKey
{
    public const int MaxNameLength = 120;

    /// <summary>Enough of the secret to recognise a key by, and nowhere near enough to use one.</summary>
    public const int PrefixLength = 12;

    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>
    /// What this key is called - "Claude in VS Code" - and, because a key is a
    /// caller, the name that lands in <c>CreatedBy</c> and in every audit row
    /// it writes. Unique, so two rows in a trail reading "Claude" are the same
    /// Claude.
    /// </summary>
    [MaxLength(MaxNameLength)]
    public required string Name { get; set; }

    /// <summary>
    /// The leading characters of the secret, for display only. A key list whose
    /// rows are distinguished only by a name somebody typed is a list you
    /// cannot check against the value in a config file - and the prefix is the
    /// only part of the secret it is safe to show, which is why it is stored
    /// separately rather than derived from a hash it cannot be derived from.
    /// </summary>
    [MaxLength(PrefixLength)]
    public required string Prefix { get; set; }

    /// <summary>SHA-256 of the whole secret. Unique so the wall's lookup is an index seek, the same shape <see cref="EfAuthGrant.TokenHash"/> takes.</summary>
    [MaxLength(AuthHash.Length)]
    public required byte[] Hash { get; set; }

    /// <summary>
    /// What this key may reach, as the names in <see cref="ApiKeyScopes"/>.
    /// Checked against the scope a <c>[RequireAdmin]</c> is willing to accept,
    /// so a key that carries none reaches nothing - which is the right default
    /// for a credential that lives in a file on somebody's laptop.
    /// </summary>
    public string[] Scopes { get; set; } = [];

    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the wall last saw this key. Written at most once per
    /// <c>Auth:LastSeenThrottleSeconds</c>, the same throttle
    /// <see cref="EfAuthGrant.LastSeenAt"/> takes and for the same reason -
    /// unthrottled it is a write on every request the key makes.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>
    /// When it stopped working, or null while it still does. A column rather
    /// than a DELETE, unlike a grant: the audit trail names this key by a name
    /// that has to keep resolving, and "who was Claude in March" is a question
    /// a deleted row cannot answer.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsLive(DateTimeOffset now) => RevokedAt is null || RevokedAt > now;

    public bool HasScope(string? scope) =>
        scope is { Length: > 0 } && Scopes.Contains(scope, StringComparer.Ordinal);
}

/// <summary>
/// The scopes a key can carry, named once so a controller's attribute and a
/// mint dialog cannot disagree about the spelling.
///
/// One entry today. A scope is a promise about a surface, and the honest number
/// of surfaces that have been thought through for a non-human caller is one -
/// Hatch, which was built for it.
/// </summary>
public static class ApiKeyScopes
{
    /// <summary>Everything under <c>/api/hatch</c>: the board, issues, comments, the importer.</summary>
    public const string Hatch = "hatch";

    /// <summary>Every scope that exists, for the mint dialog and for validating what it sends.</summary>
    public static readonly string[] All = [Hatch];

    public static bool IsKnown(string? scope) => scope is not null && All.Contains(scope, StringComparer.Ordinal);
}

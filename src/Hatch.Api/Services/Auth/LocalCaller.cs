using System.Security.Cryptography;
using System.Text;
using Hatch.Api.Common;

namespace Hatch.Api.Services.Auth;

/// <summary>
/// Who is calling when nothing authenticated them - the identity local mode
/// has instead of a wall.
///
/// <c>Auth:Enabled=false</c> already admits every request and wakes no gate
/// (docs/auth-architecture.md). What it did not have was a name: every event a
/// browser wrote against a local install read <c>operator</c>, and "Assign to
/// me" had nobody to assign to. This is the policy that gives that mode a
/// person, and gives a keyless runner a name of its own.
/// </summary>
/// <remarks>
/// <para>Kept out of <see cref="CallerIdentity"/> deliberately: every decision
/// here is a pure function of two strings, and a pure function is testable
/// without an <see cref="Microsoft.AspNetCore.Http.HttpContext"/>, a stub auth
/// service and a settings table. CallerIdentity is left holding the plumbing
/// and none of the judgement.</para>
///
/// <para><strong>Nothing here is a credential.</strong> Anybody who can set
/// <see cref="RunnerHeader"/> can also omit it, so it is a name and not a
/// proof - see docs/auth-architecture.md, "Local mode". It is worth having
/// anyway for the same reason a signature on a commit message is: the trail
/// reads correctly when everybody is honest, and local mode has no dishonest
/// party by construction, because there is no wall for one to be on the wrong
/// side of.</para>
/// </remarks>
public static class LocalCaller
{
    /// <summary>
    /// What a program calls itself when it has no key to be named by. Stripped
    /// by <see cref="AuthMiddleware"/> wherever the wall is up, so it can only
    /// ever mean something in the mode it was written for.
    /// </summary>
    public const string RunnerHeader = "X-Hatch-Runner";

    /// <summary>
    /// The name a local install uses when nobody has told it one - the same
    /// word the compose file falls back to, so there is one answer rather than
    /// two that drift.
    /// </summary>
    public const string DefaultName = "friend";

    /// <summary>
    /// The local person's id, fixed rather than derived from their name.
    ///
    /// That is the whole of the design decision. An id computed from the name
    /// would change the day somebody corrected a typo in it, and every issue
    /// assigned to them would silently read as unassigned - the liveness rule
    /// in <see cref="IActorDirectory"/> answers "nobody" for an id it does not
    /// recognise, which is correct behaviour applied to a fact that quietly
    /// became false. The id is who you are; the name is only what is drawn.
    /// </summary>
    public static readonly Guid PersonId = new("10ca1000-0000-4000-8000-000000000001");

    /// <summary>The prefix that makes a runner's derived id this app's rather than any other hash of the same string.</summary>
    private const string RunnerIdNamespace = "hatch.hatch.runner:";

    /// <summary>
    /// A stable id for a runner that named itself <paramref name="name"/>.
    ///
    /// Derived rather than random so one runner is one actor across the calls
    /// of a single increment, and derived rather than fixed so two runners on
    /// two checkouts are two. It is never written to a table and never
    /// compared against one, so a collision would cost a trail entry's
    /// distinctness and nothing else - which is why a hash is enough and a row
    /// would be a schema for something transient.
    /// </summary>
    public static Guid RunnerIdFor(string name)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(RunnerIdNamespace + name));

        var bytes = digest[..16];

        // Version 8 (custom) and the RFC 4122 variant, so the value reads as a
        // UUID everywhere rather than as sixteen bytes that happen to fit.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>
    /// What to call the person sitting at this machine: the site setting they
    /// typed, else the value the app was started with, else
    /// <see cref="DefaultName"/>.
    /// </summary>
    /// <remarks>
    /// A name that <see cref="PersonName.TryNormalize"/> refuses falls through
    /// to the next candidate rather than throwing. The caller is on the path of
    /// every request in local mode, and a misconfigured name that returned a
    /// 500 would take the whole install down over a typo in an environment
    /// variable.
    /// </remarks>
    public static string PersonNameOf(string? fromSetting, string? fromConfig)
    {
        if (PersonName.TryNormalize(fromSetting, out var setting, out _)) return setting;
        if (PersonName.TryNormalize(fromConfig, out var config, out _)) return config;

        return DefaultName;
    }

    /// <summary>
    /// Whether either candidate actually named the person, or whether
    /// <see cref="DefaultName"/> is what they are being called for want of
    /// anything better.
    /// </summary>
    /// <remarks>
    /// Beside <see cref="PersonNameOf"/> rather than derived by comparing its
    /// answer to the default, because an operator who genuinely types
    /// <c>friend</c> has configured their name and should not be told
    /// otherwise forever.
    /// </remarks>
    public static bool IsNamed(string? fromSetting, string? fromConfig) =>
        PersonName.TryNormalize(fromSetting, out _, out _) ||
        PersonName.TryNormalize(fromConfig, out _, out _);

    /// <summary>
    /// What a runner called itself, or null when it said nothing usable.
    /// </summary>
    /// <remarks>
    /// Sanitized rather than validated as a person's name: <c>host:/path</c> is
    /// what a checkout calls itself and is not a first name. The cap is
    /// <see cref="PersonName.MaxChars"/> because this lands in
    /// <c>EfHatchIssueEvent.Actor</c>, which is that column - and it is a
    /// backstop rather than the rule, since the clients that send this already
    /// clip to the same bound.
    /// </remarks>
    public static string? RunnerName(string? header)
    {
        var name = PersonName.Sanitize(header);
        if (name.Length == 0) return null;
        if (name.Length <= PersonName.MaxChars) return name;

        // Cut one char short of a lone high surrogate rather than storing half
        // a character: the column is measured in UTF-16 units and an emoji that
        // straddles the bound would otherwise be written as a replacement
        // glyph in every table that draws it.
        var cut = PersonName.MaxChars;
        if (char.IsHighSurrogate(name[cut - 1])) cut--;

        return name[..cut];
    }
}

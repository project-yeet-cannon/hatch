using Aerie.Api.Ef;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Services.Auth;

/// <summary>What the admin gate decided. Dormant and Admin both mean "serve it"; they differ in whether anyone was checked.</summary>
public enum AdminOutcome
{
    /// <summary>Enforcement is switched off, so nothing was looked at. This is the default posture and the whole rollback.</summary>
    Dormant,

    /// <summary>The caller's device belongs to a person carrying <see cref="EfPerson.IsAdmin"/>.</summary>
    Admin,

    /// <summary>Refused. What that looks like is the caller's choice: a 404 for the admin app, a 403 for the API.</summary>
    Refused,
}

/// <summary>
/// One admin decision, with the reason a refusal happened - the same shape
/// <see cref="AuthDecision"/> carries, and for the same reason: a refusal
/// nobody can explain is a support conversation with no starting point.
/// </summary>
public record AdminDecision(AdminOutcome Outcome, EfPerson? Person, string? Reason)
{
    /// <summary>No credential at all. Reachable only where the wall itself does not enforce in-process - otherwise this request never got here.</summary>
    public const string NoGrant = "no_grant";

    /// <summary>An enrolled device nobody has claimed - the hallway tablet, or a phone whose owner was never linked on the Sessions page.</summary>
    public const string NoPerson = "no_person";

    /// <summary>A device belonging to a person who is not an administrator.</summary>
    public const string NotAdmin = "not_admin";

    public bool IsAllowed => Outcome != AdminOutcome.Refused;

    public static readonly AdminDecision Dormant = new(AdminOutcome.Dormant, null, null);

    public static AdminDecision Allow(EfPerson person) => new(AdminOutcome.Admin, person, null);

    public static AdminDecision Refuse(string reason) => new(AdminOutcome.Refused, null, reason);
}

/// <summary>
/// Whether the caller may do the things only an operator should - the second
/// question the wall asks, after "is this device enrolled at all".
///
/// It is deliberately a *separate* gate from <see cref="IAuthGate"/> rather
/// than a widening of it. The wall decides whether a request reaches the app,
/// runs on every request, and is enforced in two places (Traefik and in
/// process). This decides whether an already-authenticated request may proceed,
/// runs on the handful of routes that ask, and is enforced in one place, which
/// is this process. Folding the two together would put a person lookup on the
/// health probes and the media stream, which is exactly what the allow-list
/// exists to prevent.
/// </summary>
public interface IAdminGate
{
    /// <summary>
    /// Whether this gate refuses anything at all. False leaves every route
    /// behaving exactly as it did before the flag was enforced, which is the
    /// rollback and also the default - see <see cref="AuthOptions.EnforceAdmin"/>.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// The decision for the current request. The three descriptive arguments
    /// are only ever the refusal log line's - the decision itself reads
    /// nothing but the caller's grant.
    /// </summary>
    Task<AdminDecision> EvaluateAsync(string? method, PathString path, string? clientIp, CancellationToken ct);
}

/// <summary>
/// The one place the admin flag is read. Everything that guards on it - the
/// admin app's own bundle (<see cref="Aerie.Api.Common.AdminAppMiddleware"/>)
/// and every <see cref="Aerie.Api.Common.RequireAdminAttribute"/> on the API -
/// comes through here, so "who is an administrator" has one answer and one log
/// line rather than one per asker.
///
/// Note what it does *not* do: decide whether enforcement is a good idea on
/// this install. That is <see cref="AuthOptions.EnforceAdmin"/>, an operator's
/// deliberate act, and the reason it is a switch rather than something inferred
/// from the data is written on that property.
/// </summary>
public class AdminGate(
    ICallerIdentity caller,
    IOptions<AuthOptions> options,
    ILogger<AdminGate> logger) : IAdminGate
{
    private readonly AuthOptions options = options.Value;

    /// <summary>
    /// Both switches, because an install with no wall has no identity to read.
    /// <c>Auth:Enabled</c> being false is the whole of local development and of
    /// <c>AUTH_MODE=none</c>: nobody is enrolled, no cookie is expected, and a
    /// guard that refused on that basis would make the admin app unreachable
    /// for every developer running `make run`.
    ///
    /// Deliberately *not* also gated on <c>Auth:EnforceInProcess</c>. That
    /// switch exists so Traefik can be the sole enforcer of the wall on exactly
    /// the annotated routes; this boundary has no proxy half at all, so tying
    /// it to that switch would mean the canary silently ignores an enforcement
    /// the operator asked for.
    /// </summary>
    public bool Enabled => options.Enabled && options.EnforceAdmin;

    public async Task<AdminDecision> EvaluateAsync(string? method, PathString path, string? clientIp, CancellationToken ct)
    {
        if (!Enabled) return AdminDecision.Dormant;

        var grant = await caller.GrantAsync(ct);
        if (grant is null) return Refuse(AdminDecision.NoGrant, method, path, clientIp, null);

        // The person, not the id: this is the one question in the app that
        // needs a column off the row rather than the key of it, which is why
        // ICallerIdentity carries PersonAsync at all.
        var person = await caller.PersonAsync(ct);
        if (person is null) return Refuse(AdminDecision.NoPerson, method, path, clientIp, grant);

        return person.IsAdmin
            ? AdminDecision.Allow(person)
            : Refuse(AdminDecision.NotAdmin, method, path, clientIp, grant);
    }

    /// <summary>
    /// Every refusal is a Warning, the same as the wall's, and carries the
    /// grant id rather than the label - free text an operator typed is a field
    /// nobody can filter on (docs/auth-architecture.md, "The rules that must
    /// not quietly change").
    ///
    /// It is worth more here than at the wall. A refusal at the wall is an
    /// un-enrolled device, which is ordinary; a refusal here is an enrolled
    /// household member reaching for something they cannot have, which is
    /// either a permission that wants granting or the first thing an operator
    /// would want to know about.
    /// </summary>
    private AdminDecision Refuse(string reason, string? method, PathString path, string? clientIp, EfAuthGrant? grant)
    {
        logger.LogWarning(
            "Admin refused {Reason} for {Method} {Path} from {ClientIp} (grant {GrantId}, person {PersonId})",
            reason, method, path.Value, clientIp, grant?.Id, grant?.PersonId);

        return AdminDecision.Refuse(reason);
    }
}

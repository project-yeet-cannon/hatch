namespace Hatch.Api.Services.Auth;

/// <summary>
/// Deploy-time config for the wall (the "Auth" appsettings section).
///
/// Nothing operator-specific is a literal here - the cookie domain and the
/// exempt hosts are supplied at deploy from the DOMAIN operator variable, per
/// docs/ethos.md. <see cref="Enabled"/> is the whole rollback: false leaves the
/// app behaving exactly as it did before auth existed.
///
/// Two switches, not one. <see cref="Enabled"/> decides whether the gate
/// decides at all - it is what /api/auth/verify answers on Traefik's behalf.
/// <see cref="EnforceInProcess"/> decides whether this pod also refuses on its
/// own. The chart's one auth.mode value sets both (docs/auth-architecture.md).
/// </summary>
public class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Whether the gate refuses anything at all - false is the whole rollback
    /// (docs/auth-architecture.md), and it is false in Development so `make run`
    /// stays frictionless; flip it locally with
    /// <c>Auth__Enabled=true dotnet run</c>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether the pod refuses on its own, or only answers
    /// <c>/api/auth/verify</c> on Traefik's behalf. True everywhere except the
    /// canary, where the point is that Traefik enforces on exactly the
    /// routes carrying the middleware annotation and nothing else - one app,
    /// rather than the whole pod.
    ///
    /// Read only by <see cref="Hatch.Api.Common.AuthMiddleware"/>;
    /// <see cref="AuthGate"/> never sees it, so <c>verify</c> keeps deciding
    /// for real whenever <see cref="Enabled"/> is true. Defaults to true, so
    /// this cannot quietly widen what an existing config means: the canary has
    /// to ask for it.
    ///
    /// Stated honestly: while this is false, anything reaching the Service
    /// directly - in-cluster, or a <c>kubectl port-forward</c> - is as open as
    /// it was before auth existed. That is the pre-auth posture, held
    /// deliberately for as long as the canary is the mode.
    /// </summary>
    public bool EnforceInProcess { get; set; } = true;

    /// <summary>
    /// Whether <see cref="Hatch.Api.Ef.EfPerson.IsAdmin"/> decides anything -
    /// the admin app's bundle, and the API actions carrying
    /// <see cref="Hatch.Api.Common.RequireAdminAttribute"/>. False everywhere
    /// until an operator says otherwise, and false is the rollback.
    ///
    /// A switch rather than something inferred from the data, and the reason is
    /// the lockout this whole column was carried early to avoid. Every
    /// alternative shape - "enforce once somebody is an admin", "enforce once
    /// an admin holds a device" - makes a checkbox on the People page the thing
    /// that turns enforcement on, which means an operator can lock the
    /// household out of the page they would fix it from without ever intending
    /// to enable anything. Config cannot be tripped over: turning this on is a
    /// deploy, turning it off again is the same deploy, and neither is
    /// reachable from inside a browser session that is about to lose its
    /// access.
    ///
    /// Read only through <see cref="AdminGate.Enabled"/>, which also requires
    /// <see cref="Enabled"/>: an install with no wall has no identity to read,
    /// so enforcing a role there would refuse everyone.
    /// </summary>
    public bool EnforceAdmin { get; set; }

    /// <summary>
    /// Who is sitting at the machine when there is no wall to ask - read only
    /// when <see cref="Enabled"/> is false. See
    /// <see cref="LocalCaller"/> and docs/auth-architecture.md, "Local mode".
    /// </summary>
    public LocalPersonOptions LocalPerson { get; set; } = new();

    /// <summary>
    /// The cookie the grant token rides in. The <c>__Secure-</c> prefix is
    /// correct rather than <c>__Host-</c>: <c>__Host-</c> forbids a Domain
    /// attribute, and the Domain attribute is the entire single-sign-on story
    /// here - it's what makes one enrollment cover home., kiosk. and share.
    /// Local dev over http has to override this, since browsers reject a
    /// <c>__Secure-</c> cookie that arrives without TLS.
    /// </summary>
    public string CookieName { get; set; } = "__Secure-hatch_grant";

    /// <summary>
    /// The cookie's Domain attribute, e.g. ".example.com" - one enrollment
    /// across every subdomain. Empty (the default, and the only sane value on
    /// localhost) makes the cookie host-only.
    /// </summary>
    public string CookieDomain { get; set; } = "";

    /// <summary>
    /// Hostnames the gate never challenges, whatever the path - the hosts whose
    /// Ingress simply never gets the middleware annotation, repeated here so
    /// the in-process gate agrees with the proxy. files.&lt;domain&gt; is the
    /// standing example: the kiosk APK and its checksum are public by design.
    /// </summary>
    public string[] ExemptHosts { get; set; } = [];

    /// <summary>How long an invite code is worth typing. Short because the code is only 40 bits; see EfAuthInvite.</summary>
    public int InviteTtlMinutes { get; set; } = 15;

    /// <summary>
    /// TTL for the invite the migrate Job mints into an empty install. Longer
    /// than a normal one because nobody is standing at the tablet when a deploy
    /// finishes - it has to survive the walk from the terminal to the room.
    /// </summary>
    public int BootstrapInviteTtlMinutes { get; set; } = 60;

    /// <summary>
    /// Re-issue the cookie once it's this old. Comfortably inside Chrome's
    /// 400-day Max-Age cap, so an in-use device never lapses; a device that
    /// goes untouched past the cap re-enrolls, which is the intended failure.
    /// </summary>
    public int GrantRenewAfterDays { get; set; } = 30;

    /// <summary>
    /// Floor on how often a grant's LastSeenAt is written. This runs on every
    /// request through the wall, so an unthrottled write is a write amplifier
    /// on the hottest path in the app - a minute of staleness on a "last seen"
    /// column costs nothing and saves every one of those writes.
    /// </summary>
    public int LastSeenThrottleSeconds { get; set; } = 60;

    /// <summary>Where a document request is sent when it has no grant. Exempt by construction, or the redirect is a loop.</summary>
    public string SignInPath { get; set; } = "/apps/auth/";

    /// <summary>
    /// Redemption attempts one client IP may spend per
    /// <see cref="RedeemWindowSeconds"/>. Bounds the guessing rate against a
    /// 40-bit code; it is not the only defence, and deliberately not the main
    /// one - the TTL, single use, and the Warning log per refusal are.
    /// </summary>
    public int RedeemAttemptsPerWindow { get; set; } = 10;

    /// <summary>The window <see cref="RedeemAttemptsPerWindow"/> is counted over.</summary>
    public int RedeemWindowSeconds { get; set; } = 60;

    public TimeSpan InviteTtl => TimeSpan.FromMinutes(Math.Max(1, InviteTtlMinutes));

    public TimeSpan BootstrapInviteTtl => TimeSpan.FromMinutes(Math.Max(1, BootstrapInviteTtlMinutes));

    public TimeSpan GrantRenewAfter => TimeSpan.FromDays(Math.Max(1, GrantRenewAfterDays));

    public TimeSpan LastSeenThrottle => TimeSpan.FromSeconds(Math.Max(0, LastSeenThrottleSeconds));

    public TimeSpan RedeemWindow => TimeSpan.FromSeconds(Math.Max(1, RedeemWindowSeconds));
}

/// <summary>
/// The name local mode answers "who am I" with (the "Auth:LocalPerson"
/// subsection, so <c>Auth__LocalPerson__Name</c> is the environment spelling).
///
/// Its own class rather than a flat <c>Auth:LocalPersonName</c> because the
/// next thing local mode needs to know about the person - an avatar, a
/// timezone - belongs beside this one rather than in a fourth key with a
/// prefix in its name.
/// </summary>
public class LocalPersonOptions
{
    /// <summary>
    /// What to call whoever started the app. Empty is the ordinary state:
    /// nothing supplied it, and <see cref="LocalCaller.DefaultName"/> is the
    /// answer.
    ///
    /// Deliberately <em>not</em> defaulted to <c>Environment.UserName</c>.
    /// Inside a container that is the container's user - <c>root</c>, or
    /// <c>app</c> - which is a confident wrong answer where an honest fallback
    /// belongs. The compose file passes the host's user in explicitly, which is
    /// the one place that knows it.
    /// </summary>
    public string Name { get; set; } = "";
}

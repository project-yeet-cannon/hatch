using Hatch.Api.Ef;
using Hatch.Api.Services.Media;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Hatch.Api.Services.Auth;

/// <summary>What the gate decided. Allow and Authenticated both mean "serve it"; they differ in whether anyone was identified.</summary>
public enum AuthOutcome
{
    /// <summary>Exempt - on the allow-list, on an exempt host, or the gate is switched off. No credential was looked at.</summary>
    Allow,

    /// <summary>A live credential was presented - a grant off a cookie, or an API key off an Authorization header.</summary>
    Authenticated,

    /// <summary>Refused. The caller decides what a refusal looks like: a redirect for a document request, a bare 401 for everything else.</summary>
    Challenge,
}

/// <summary>
/// One gate decision, with the reason a refusal happened - which is what the
/// Warning log line carries, and eventually what a support conversation starts
/// from.
/// </summary>
public record AuthDecision(AuthOutcome Outcome, EfAuthGrant? Grant, string? Reason, string? Token = null, EfApiKey? ApiKey = null)
{
    public const string NoCredential = "no_credential";
    public const string UnknownGrant = "unknown_grant";

    /// <summary>A bearer key that no live row answers to - never minted, mistyped, or revoked. One reason for all three, as with a grant.</summary>
    public const string UnknownKey = "unknown_key";

    public bool IsAllowed => Outcome != AuthOutcome.Challenge;

    public static readonly AuthDecision Exempt = new(AuthOutcome.Allow, null, null);

    /// <summary>Carries the token that verified, so the sliding re-issue writes back the same secret rather than guessing which cookie won.</summary>
    public static AuthDecision Authenticated(EfAuthGrant grant, string token) => new(AuthOutcome.Authenticated, grant, null, token);

    /// <summary>
    /// The other lane. No token, because there is no cookie to re-issue: a
    /// program holding a key already has its credential in a file, and handing
    /// it a Set-Cookie would be the wall inventing a session nobody asked for.
    /// </summary>
    public static AuthDecision AuthenticatedKey(EfApiKey key) => new(AuthOutcome.Authenticated, null, null, null, key);

    public static AuthDecision Challenge(string reason) => new(AuthOutcome.Challenge, null, reason);
}

public interface IAuthGate
{
    /// <summary>Whether the gate refuses anything at all. False leaves the app exactly as it behaved before auth existed.</summary>
    bool Enabled { get; }

    /// <summary>Whether this path and host bypass the wall without presenting anything. Public so a caller can skip the credential lookup entirely.</summary>
    bool IsExempt(PathString path, string? host);

    /// <summary>
    /// Whether an exempt path should still have its caller *identified* - let
    /// through either way, but with a name attached if one is presented.
    ///
    /// This exists because "exempt" and "anonymous" are not the same thing, and
    /// conflating them silently empties a field. See the remarks on
    /// <see cref="AuthGate.IdentifiesWithoutEnforcing"/>.
    /// </summary>
    bool IdentifiesWithoutEnforcing(PathString path);

    /// <summary>
    /// The decision for one request. <paramref name="path"/> and
    /// <paramref name="host"/> are the *original* request's, which for the
    /// forwardAuth endpoint means the ones Traefik forwarded rather than the
    /// proxied request's own.
    /// </summary>
    /// <param name="tokens">Grant tokens off the cookie jar - plural, because a browser can present several cookies of one name.</param>
    /// <param name="bearer">
    /// The secret from an <c>Authorization: Bearer</c> header, or null. A
    /// distinct argument rather than a fourth entry in
    /// <paramref name="tokens"/>: it is a different credential answered by a
    /// different table, and a list that mixed the two would be a list where a
    /// key could be tried as a cookie.
    /// </param>
    Task<AuthDecision> EvaluateAsync(
        PathString path, string? host, IReadOnlyList<string> tokens, string? bearer, string? clientIp, CancellationToken ct);
}

/// <summary>
/// The one place the wall decides anything, shared by the in-process middleware
/// and the Traefik forwardAuth endpoint. Two gates with two allow-lists is one
/// allow-list too many: the failure mode is a path that is open through the
/// proxy and closed inside the cluster, or the reverse, and neither looks like
/// an auth bug from the outside.
///
/// The allow-list below is load-bearing and every entry is there for a stated
/// reason - see docs/auth-architecture.md, "The allow-list is load-bearing". Two of
/// them fail silently and confusingly if they are ever dropped: the health
/// probes (gating them fails readiness on every pod, and the Deployment never
/// becomes available) and the media library (Sonos speakers fetch the stream
/// themselves and cannot hold a cookie, so gating it stops all music with no
/// error that mentions authentication).
/// </summary>
public class AuthGate(
    IAuthService auth,
    IOptions<AuthOptions> options,
    IOptions<MediaLibraryOptions> mediaLibrary,
    ILogger<AuthGate> logger) : IAuthGate
{
    private readonly AuthOptions options = options.Value;

    /// <summary>
    /// Prefixes that never see the wall. Matched by path segment, so /media is
    /// exempt and /mediafoo is not - a StartsWith on the raw string would open
    /// far more than this list names.
    /// </summary>
    private readonly PathString[] exemptPaths =
    [
        // Kubernetes probes present no cookie. Gating these fails readiness on
        // every pod and the Deployment never becomes available - a total
        // outage whose cause looks nothing like authentication.
        "/health/live",
        "/health/ready",

        // Read from MediaLibraryOptions rather than hardcoded, because the
        // prefix is deploy-time config and the two drifting apart is exactly
        // the silent failure this list exists to prevent.
        NormalizePrefix(mediaLibrary.Value.RequestPath),

        // Browser log shipping, including from the sign-in shell itself. A
        // gated log endpoint means the failures you most want to see are the
        // ones that can't report.
        "/api/ui-logs",

        // Which commit this replica is running. The value names a commit of a
        // repository headed for public release, and the caller who most needs
        // it is the one holding a stale build that is about to be refused -
        // gating it would hide the answer from exactly that caller. The
        // cluster's own state is *not* part of the unauthenticated answer; the
        // controller withholds that separately.
        "/api/hatch-revision",

        // Server-to-server from Hyper-V scheduled tasks, which hold no cookie -
        // and it carries its own X-Vm-Log-Token gate, which is strictly
        // stronger than a cookie would be (VmConsoleLogsController).
        "/api/vm-console-logs",

        // Tablet provisioning stays friction-free: nothing behind it is more
        // sensitive than an APK URL and the Wi-Fi credentials the tablet is
        // about to join with anyway.
        "/api/kiosk/provisioning-info",

        // The kiosk *shell* drives the tablet backlight from the day's sun
        // events (DisplayController.kt) and calls this over plain
        // HttpURLConnection, which shares no cookie jar with the GeckoView the
        // page runs in - the page's grant cannot cover it. Exposed: sunrise and
        // sunset times, from which the site's approximate latitude is
        // inferable. That is strictly less than the line above already hands
        // out, and the endpoint takes lat/lon overrides, so it is a solar
        // calculator far more than it is a location.
        "/api/sun-events",

        // And the other call that same shell makes over the same cookie-less
        // HttpURLConnection: the origin health probe that decides whether the
        // page it just loaded is the dashboard or the proxy's error page
        // (MainActivity.probeOriginHealth). Gating it is not a degraded probe,
        // it is a bricked wall display - the shell reads the 401 as "Hatch is
        // reachable but isn't answering", covers a dashboard that is rendering
        // perfectly well underneath, and retries that forever. The tablet's own
        // grant cannot help: it lives in the GeckoView's cookie jar, which is
        // the whole reason the row above exists.
        //
        // The prefix, not just /dashboard: every app under wwwroot/apps answers
        // here, the answer is a list of the bundle's content-hashed asset
        // filenames, and that is strictly less than /api/hatch-revision
        // already hands out.
        "/api/app-version",

        // The sign-in shell and the two endpoints it calls. Gating these is an
        // infinite redirect loop. Its short alias /auth is exempt too, just
        // below - exactly rather than by prefix.
        "/apps/auth",
        "/api/auth/verify",
        "/api/auth/redeem",
    ];

    /// <summary>
    /// Exempt exactly, rather than as a prefix. <c>/auth</c> is the short form
    /// of the sign-in URL - short enough to read out over the phone to someone
    /// holding a new tablet - and it redirects into <c>/apps/auth/</c>
    /// (Program.cs). It has to work for a caller who by definition has no
    /// grant yet, and it has to be exempt *here* rather than by moving the
    /// rewriter: in production the decision is made by Traefik asking about the
    /// original URI, which never reaches this app's rewrite rules at all.
    ///
    /// Matched exactly because a prefix would silently exempt whatever is
    /// mounted underneath it later. This list opens what it names and nothing
    /// else.
    /// </summary>
    private static readonly PathString[] exemptExactPaths = ["/auth", "/auth/"];

    /// <summary>
    /// Exempt paths whose caller is still worth identifying. Deliberately one
    /// entry rather than "all of them": identification costs a credential
    /// lookup, and the reason the list above exists is that the health probes
    /// and the media stream must cost nothing.
    ///
    /// The one entry is log shipping, and it is here because of a bug this
    /// method exists to fix. UiLogsController has always logged an `actor`
    /// field read from the authenticated grant - but /api/ui-logs is on the
    /// allow-list, so the middleware returned before ever setting one, and the
    /// field was silently null on every line the app has ever shipped. A gate
    /// that must not refuse a request is not the same as a gate that must not
    /// look at it: refusing log shipping would hide exactly the failures worth
    /// seeing, while declining to identify it costs the operator the one field
    /// that says whose browser a page load came from.
    /// </summary>
    private static readonly PathString[] identifyOnlyPaths = ["/api/ui-logs"];

    public bool Enabled => options.Enabled;

    public bool IdentifiesWithoutEnforcing(PathString path)
    {
        foreach (var prefix in identifyOnlyPaths)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public bool IsExempt(PathString path, string? host)
    {
        if (IsExemptHost(host)) return true;

        foreach (var prefix in exemptPaths)
        {
            if (prefix.HasValue && path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var exact in exemptExactPaths)
        {
            if (path.Equals(exact, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public async Task<AuthDecision> EvaluateAsync(
        PathString path, string? host, IReadOnlyList<string> tokens, string? bearer, string? clientIp, CancellationToken ct)
    {
        // Off is the rollback, and it is also the whole of local dev. Deciding
        // it here rather than in each caller means there is no way to reach the
        // wall through a caller that forgot to check.
        if (!options.Enabled) return AuthDecision.Exempt;

        if (IsExempt(path, host)) return AuthDecision.Exempt;

        // The bearer lane goes first, and its refusal is final rather than a
        // fall-through to the cookie. A caller that sent a key meant to send a
        // key: quietly trying its cookies afterwards would answer 200 to a
        // request made with a revoked credential whenever the same browser
        // happened to be signed in, which is precisely the case revocation
        // exists to close.
        if (!string.IsNullOrEmpty(bearer))
        {
            var key = await auth.VerifyApiKeyAsync(bearer, ct);
            return key is null
                ? Refuse(AuthDecision.UnknownKey, path, host, clientIp)
                : AuthDecision.AuthenticatedKey(key);
        }

        if (tokens.Count == 0) return Refuse(AuthDecision.NoCredential, path, host, clientIp);

        var verified = await auth.VerifyAsync(tokens, clientIp, ct);
        return verified is null
            ? Refuse(AuthDecision.UnknownGrant, path, host, clientIp)
            : AuthDecision.Authenticated(verified.Grant, verified.Token);
    }

    /// <summary>
    /// Every refusal is a Warning carrying the reason and the client IP. That
    /// is what makes a brute-force attempt visible on logs.&lt;domain&gt;
    /// through the existing fluent-bit pipeline, without building alerting for
    /// it first.
    /// </summary>
    private AuthDecision Refuse(string reason, PathString path, string? host, string? clientIp)
    {
        logger.LogWarning(
            "Auth refused {Reason} for {Host}{Path} from {ClientIp}",
            reason, host, path.Value, clientIp);
        return AuthDecision.Challenge(reason);
    }

    private bool IsExemptHost(string? host)
    {
        if (options.ExemptHosts.Length == 0 || string.IsNullOrEmpty(host)) return false;

        // Host headers carry a port and the exempt list doesn't, so compare the
        // name alone - otherwise an exemption written as "files.example.com"
        // silently stops applying the moment a request arrives on :8080.
        var name = HostString.FromUriComponent(host).Host;
        foreach (var exempt in options.ExemptHosts)
        {
            if (name.Equals(exempt.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>A PathString has to be rooted and unslashed; MediaLibrary:RequestPath is operator-supplied and may be neither.</summary>
    private static PathString NormalizePrefix(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed)) return PathString.Empty;

        return new PathString(trimmed.StartsWith('/') ? trimmed : "/" + trimmed);
    }
}

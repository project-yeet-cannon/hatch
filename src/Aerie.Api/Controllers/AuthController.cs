using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Models.Auth;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Controllers;

/// <summary>
/// The endpoints the wall needs: the one Traefik asks, the one that turns an
/// invite code into a session, the two a signed-in device uses to ask who it is
/// and to stop being anyone, and the three behind the admin Sessions page that
/// hand sessions out and take them away.
///
/// Two of them - verify and redeem - are on the gate's allow-list by necessity,
/// since gating the sign-in path is an infinite redirect loop. That is why
/// nothing here reads a request header as an identity, and why everything reads
/// the cookie through the same <see cref="IAuthGate"/> the middleware uses.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController(
    IAuthGate gate,
    IAuthService auth,
    ICallerIdentity caller,
    IOptions<AuthOptions> options,
    ILogger<AuthController> logger) : ControllerBase
{
    /// <summary>Named so Program.cs can configure the partition and this file can apply it, without either restating the numbers.</summary>
    public const string RedeemRateLimitPolicy = "auth-redeem";

    /// <summary>Why a link was refused - the person is gone, so the Sessions page is holding a stale dropdown. See <see cref="LinkGrantPerson"/>.</summary>
    public const string UnknownPersonError = "unknown_person";

    /// <summary>Why a revocation was refused - the caller aimed it at the device it is sitting on. See <see cref="RevokeGrant"/>.</summary>
    public const string OwnGrantError = "own_grant";

    private const string ForwardedMethod = "X-Forwarded-Method";
    private const string ForwardedHost = "X-Forwarded-Host";
    private const string ForwardedProto = "X-Forwarded-Proto";
    private const string ForwardedUri = "X-Forwarded-Uri";

    /// <summary>Any relative URI resolves against this and nothing leaves it - it exists only so <see cref="Uri"/> will do a server's path normalization for us.</summary>
    private static readonly Uri ForwardedBase = new("http://forwarded.invalid", UriKind.Absolute);

    private readonly AuthOptions options = options.Value;

    /// <summary>
    /// The Traefik forwardAuth target. Traefik proxies a copy of the original
    /// request here, so this request's own method, host and path are always
    /// GET / this pod / /api/auth/verify - the ones that matter arrive in
    /// X-Forwarded-Method / -Host / -Uri, and everything else (Accept,
    /// Sec-Fetch-Mode, Cookie) is the original's, unchanged.
    /// </summary>
    [HttpGet("verify")]
    public async Task<IActionResult> Verify(CancellationToken ct)
    {
        var forwardedUri = Request.Headers[ForwardedUri].ToString();
        var method = Request.Headers[ForwardedMethod].ToString();
        var host = Request.Headers[ForwardedHost].ToString();

        // Fail closed when Traefik didn't say what it is asking about. Falling
        // back to this request's own path would answer 204 to everything the
        // moment those headers stopped arriving, because /api/auth/verify is
        // itself on the allow-list - a gate that opens when it breaks is worse
        // than no gate, because it looks like one.
        var path = ForwardedPath(forwardedUri);

        var decision = await gate.EvaluateAsync(
            path,
            string.IsNullOrEmpty(host) ? Request.Host.Value : host,
            AuthCookie.ReadAll(Request, options),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct);

        if (decision.Outcome == AuthOutcome.Challenge)
        {
            Response.Headers.CacheControl = "no-store";

            if (AuthChallenge.PrefersRedirect(Request.Headers, method))
            {
                // Absolute here, and *only* here. Traefik resolves a redirect
                // from this endpoint against its own request to it, so the
                // relative form the in-process middleware returns would reach
                // the browser as http://api.<ns>.svc.cluster.local:8080/apps/auth/
                // - unreachable from the LAN, and a wall that looks like it is
                // working right up until the sign-in page fails to load. The
                // origin is rebuilt from the forwarded headers, so kiosk. still
                // stays on kiosk.; ?r= stays a relative path regardless.
                Response.Headers.Location = AuthChallenge.SignInLocation(
                    options,
                    forwardedUri,
                    Request.Headers[ForwardedProto].ToString(),
                    host);
                return StatusCode(StatusCodes.Status302Found);
            }

            return Unauthorized();
        }

        // Copied onto the proxied request by Traefik's authResponseHeaders, and
        // stripped from any request that arrives carrying them (AuthMiddleware),
        // so downstream they mean exactly one thing.
        if (decision.Grant is { } grant)
        {
            Response.Headers[AuthChallenge.GrantHeader] = grant.Id.ToString();
            Response.Headers[AuthChallenge.LabelHeader] = HeaderSafe(grant.Label);
        }

        return NoContent();
    }

    /// <summary>
    /// Turns an invite code into a session. The only endpoint in the app that
    /// mints a credential, which is why it is the only one that is rate
    /// limited.
    /// </summary>
    [HttpPost("redeem")]
    [EnableRateLimiting(RedeemRateLimitPolicy)]
    public async Task<IActionResult> Redeem([FromBody] RedeemRequest request, CancellationToken ct)
    {
        var result = await auth.RedeemAsync(
            request?.Code,
            request?.Label,
            Request.Headers.UserAgent.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct);

        if (!result.Succeeded) return BadRequest(new AuthErrorDto(result.Error!));

        AuthCookie.Issue(Response, options, result.Token!);
        return Ok(AuthGrantDto.From(result.Grant!, isCurrent: true));
    }

    /// <summary>Who this device is. Gated in the ordinary way, so an unenrolled caller gets the same refusal it would get anywhere else.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var grant = await CurrentGrantAsync(ct);
        return grant is null ? Unauthorized() : Ok(AuthGrantDto.From(grant, isCurrent: true));
    }

    /// <summary>
    /// Stops being anyone. Revocation is deletion, so this is the same
    /// operation the admin Sessions page performs on someone else's device -
    /// there is no second notion of a "logged out but still enrolled" grant to
    /// keep consistent.
    /// </summary>
    [HttpPost("sign-out")]
    public async Task<IActionResult> SignOutDevice(CancellationToken ct)
    {
        if (await CurrentGrantAsync(ct) is { } grant)
        {
            await auth.RevokeGrantAsync(grant.Id, ct);
            logger.LogInformation("Grant {GrantId} ({Label}) signed itself out", grant.Id, grant.Label);
        }

        // Unconditionally, and whether or not there was a grant: a cookie
        // holding a token no row answers to is exactly the state that makes
        // "sign out and try again" fail to fix anything.
        AuthCookie.Clear(Response, options);
        return NoContent();
    }

    /// <summary>
    /// Every enrolled device, newest first - the whole of the admin Sessions
    /// page's list.
    ///
    /// One of the two guarded *reads* in the app (SettingsController is the
    /// other), and the exception is easy to justify: this is the inventory of
    /// every credential in the household, with the label, the owner, the last
    /// IP and the user agent of each. It is the list you would want before
    /// deciding which device to take. Every other GET in Aerie stays open,
    /// because every other GET answers a question about the house rather than
    /// about who can get into it.
    /// </summary>
    [RequireAdmin]
    [HttpGet("grants")]
    public async Task<IActionResult> ListGrants(CancellationToken ct)
    {
        // Resolved before the list so the caller's own row can be marked, which
        // is what keeps the Sessions page from offering someone the button that
        // ends their own visit to it.
        var current = await CurrentGrantAsync(ct);
        var grants = await auth.ListGrantsAsync(ct);

        return Ok(grants.Select(grant => AuthGrantDto.From(grant, grant.Id == current?.Id)).ToList());
    }

    /// <summary>
    /// Revokes someone else's device. Deletion is the whole of revocation -
    /// there is no revoked-but-still-enrolled state to keep consistent.
    ///
    /// It refuses the caller's own grant, and that is not squeamishness about
    /// lockout: an operator can always mint another invite. It is about what
    /// this path structurally cannot do, which is clear the cookie on a browser
    /// it is not answering. Deleting your own row here would leave that browser
    /// presenting a token no row answers to - the exact state that makes "sign
    /// out and back in" fail to fix anything. <see cref="SignOutDevice"/> does
    /// both halves, and is what the Sessions page offers on that one row.
    /// </summary>
    [RequireAdmin]
    [HttpDelete("grants/{id:guid}")]
    public async Task<IActionResult> RevokeGrant(Guid id, CancellationToken ct)
    {
        if (await CurrentGrantAsync(ct) is { Id: var currentId } && currentId == id)
        {
            return BadRequest(new AuthErrorDto(OwnGrantError));
        }

        return await auth.RevokeGrantAsync(id, ct) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Claims a device for a person, or unclaims it with a null person.
    ///
    /// This is the *only* write path for that link, and it lives on the grant
    /// rather than on the person because that is the shape of the data: a
    /// session has at most one person, so this is a single value on a row that
    /// already exists. Inverting it - editing a person's list of sessions -
    /// would turn a one-to-many into a multi-picker, and would put the control
    /// on a page nobody is looking at during the one moment it is wanted, which
    /// is while enrolling the device.
    ///
    /// Nothing about the wall changes here. The grant is as valid before as
    /// after; this writes a name onto it, and no gate reads it.
    /// </summary>
    [RequireAdmin]
    [HttpPut("grants/{id:guid}/person")]
    public async Task<IActionResult> LinkGrantPerson(Guid id, [FromBody] LinkPersonRequest? request, CancellationToken ct)
    {
        var result = await auth.SetGrantPersonAsync(id, request?.PersonId, ct);

        return result switch
        {
            GrantLinkResult.NoSuchGrant => NotFound(),
            GrantLinkResult.NoSuchPerson => BadRequest(new AuthErrorDto(UnknownPersonError)),
            _ => NoContent(),
        };
    }

    /// <summary>
    /// Mints an invite for whoever is standing next to the operator. The
    /// response body carries the code in plaintext - the only moment it exists
    /// outside a hash - so it can be drawn as a QR and read aloud, and never
    /// again after the tab is closed. A lost code is replaced by minting
    /// another, not by looking this one up.
    ///
    /// Guarded, which puts the only way to enrol a device behind being an
    /// administrator - so an install that turns Auth:EnforceAdmin on with
    /// nobody flagged can no longer let anybody in. That is not an oversight
    /// and it is not unrecoverable: the migrate Job still mints a bootstrap
    /// invite into an install with no live access (Program.cs), and the switch
    /// itself is deploy-time config precisely so that the way out of this is a
    /// deploy rather than a database edit. See docs/auth-architecture.md,
    /// "Bootstrap and lockout recovery".
    /// </summary>
    [RequireAdmin]
    [HttpPost("invites")]
    public async Task<IActionResult> CreateInvite([FromBody] CreateInviteRequest? request, CancellationToken ct)
    {
        var invite = await auth.CreateInviteAsync(request?.Label, request?.PersonId, isBootstrap: false, ct);

        // A live credential has no business in a cache, anyone's.
        Response.Headers.CacheControl = "no-store";

        logger.LogInformation("Invite {InviteId} generated for {Label}", invite.Id, invite.Label ?? "an unnamed device");

        return Ok(new AuthInviteDto(
            invite.Id,
            invite.Code,
            invite.FormattedCode,
            RedeemPath(invite.Code),
            invite.ExpiresAt,
            invite.Label));
    }

    /// <summary>
    /// Where a scanned QR lands, built from the same Auth:SignInPath the
    /// refusal redirect uses - so an install that mounts the sign-in shell
    /// somewhere else moves both at once, and a code can't be handed out
    /// pointing at a shell that isn't there.
    /// </summary>
    private string RedeemPath(string code) => $"{options.SignInPath.TrimEnd('/')}/r/{code}";

    /// <summary>
    /// The grant behind this request. The wall-is-down fallback this used to
    /// spell out for itself now lives in <see cref="ICallerIdentity"/>, because
    /// Quill needed the same answer and two copies of "who is this" is one too
    /// many.
    /// </summary>
    private Task<EfAuthGrant?> CurrentGrantAsync(CancellationToken ct) => caller.GrantAsync(ct);

    /// <summary>
    /// The path the origin server will actually serve, from the URI Traefik
    /// forwarded. Uri does the percent-decoding and the dot-segment collapsing
    /// in one pass and in a server's order, which is what closes the gap the
    /// allow-list would otherwise leave: /media/%2e%2e/apps/admin is exempt if
    /// you only look at its prefix, and is /apps/admin by the time Kestrel
    /// serves it.
    /// </summary>
    private static PathString ForwardedPath(string? forwardedUri)
    {
        if (string.IsNullOrEmpty(forwardedUri) || forwardedUri[0] != '/') return "/";
        if (!Uri.TryCreate(ForwardedBase, forwardedUri, out var absolute)) return "/";

        return PathString.FromUriComponent(absolute);
    }

    /// <summary>
    /// A label is free text an admin typed, and this one is about to become a
    /// response header. Anything outside printable ASCII becomes '?' rather
    /// than a second header line.
    /// </summary>
    private static string HeaderSafe(string label) =>
        new(label.Take(128).Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());
}

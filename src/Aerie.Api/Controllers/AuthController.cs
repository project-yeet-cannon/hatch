using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Models.Auth;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Controllers;

/// <summary>
/// The four endpoints the wall needs: the one Traefik asks, the one that turns
/// an invite code into a session, and the two a signed-in device uses to ask
/// who it is and to stop being anyone.
///
/// Three of them are on the gate's allow-list by necessity - gating the
/// sign-in path is an infinite redirect loop - which is why nothing here reads
/// a request header as an identity and everything reads the cookie through the
/// same <see cref="IAuthGate"/> the middleware uses.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController(
    IAuthGate gate,
    IAuthService auth,
    IOptions<AuthOptions> options,
    ILogger<AuthController> logger) : ControllerBase
{
    /// <summary>Named so Program.cs can configure the partition and this file can apply it, without either restating the numbers.</summary>
    public const string RedeemRateLimitPolicy = "auth-redeem";

    private const string ForwardedMethod = "X-Forwarded-Method";
    private const string ForwardedHost = "X-Forwarded-Host";
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
            AuthCookie.Read(Request, options),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct);

        if (decision.Outcome == AuthOutcome.Challenge)
        {
            Response.Headers.CacheControl = "no-store";

            if (AuthChallenge.PrefersRedirect(Request.Headers, method))
            {
                // Relative, so the browser resolves it against whichever host it
                // was actually going to - kiosk. stays on kiosk. - and so ?r=
                // never carries an absolute URL anywhere near the sign-in shell.
                Response.Headers.Location = AuthChallenge.SignInLocation(options, forwardedUri);
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
    /// The grant behind this request. The middleware has usually already
    /// resolved it, but it only runs when Auth:Enabled is true - and "who am I"
    /// has to answer the same way with the wall down, which is every phase
    /// before 5 and all of local dev.
    /// </summary>
    private async Task<EfAuthGrant?> CurrentGrantAsync(CancellationToken ct) =>
        HttpContext.GetAuthGrant()
        ?? await auth.VerifyAsync(
            AuthCookie.Read(Request, options),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct);

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

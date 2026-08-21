using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Common;

/// <summary>
/// The wall, in-process. Traefik's forwardAuth (phase 5) is the outer gate and
/// this is the inner one, both calling the same <see cref="IAuthGate"/> so
/// there is one allow-list rather than two that can disagree.
///
/// It is not redundant. A pod reached directly inside the cluster - by another
/// workload, or by a `kubectl port-forward` - never passes through Traefik at
/// all, and under `make run` there is no proxy in the picture whatsoever, which
/// makes this the *only* gate in local development. It also does the one thing
/// forwardAuth structurally cannot: re-issue the cookie, since Traefik copies
/// only the headers named in authResponseHeaders back from the auth response
/// and Set-Cookie is not among them.
///
/// Registered after UseForwardedHeaders (it needs the real client IP for the
/// refusal log) and before the /apps static file handlers (otherwise the SPA
/// bundles serve to anyone). No-ops entirely when Auth:Enabled is false, and
/// also when Auth:EnforceInProcess is false - the phase 5 canary, which needs
/// Traefik to be the only enforcer for exactly one phase.
/// </summary>
public class AuthMiddleware(RequestDelegate next, IOptions<AuthOptions> options, ILogger<AuthMiddleware> logger)
{
    private readonly AuthOptions options = options.Value;

    public async Task InvokeAsync(HttpContext context, IAuthGate gate, IAuthService auth, TimeProvider time)
    {
        // Unconditionally, before anything else and whether or not the gate is
        // on: an inbound X-Aerie-Grant is a forgery. Traefik sets these on the
        // *proxied* request from its own authResponseHeaders, so a client that
        // reaches a pod directly could otherwise hand itself an identity that
        // downstream code has every reason to believe.
        context.Request.Headers.Remove(AuthChallenge.GrantHeader);
        context.Request.Headers.Remove(AuthChallenge.LabelHeader);

        // Two conditions, one no-op. Off is the rollback; on-but-not-enforcing
        // is the phase 5 canary, where Traefik is deliberately the only
        // enforcer so that the wall stands in front of exactly the routes
        // carrying its annotation. /api/auth/verify still decides for real in
        // both cases, because it goes through the gate rather than through
        // here.
        if (!gate.Enabled || !options.EnforceInProcess)
        {
            await next(context);
            return;
        }

        var request = context.Request;
        var host = request.Host.Value;

        // Checked before the cookie is even read, so an exempt path costs
        // nothing on the hot paths that need it most - the health probes and
        // every byte Sonos streams out of /media.
        if (gate.IsExempt(request.Path, host))
        {
            await next(context);
            return;
        }

        var token = AuthCookie.Read(request, options);
        var clientIp = context.Connection.RemoteIpAddress?.ToString();
        var decision = await gate.EvaluateAsync(request.Path, host, token, clientIp, context.RequestAborted);

        if (decision.Outcome == AuthOutcome.Challenge)
        {
            Refuse(context);
            return;
        }

        if (decision.Grant is { } grant)
        {
            context.SetAuthGrant(grant);
            await RenewCookieIfStaleAsync(context, auth, grant, token, time);
        }

        await next(context);
    }

    /// <summary>
    /// The sliding window that makes an in-use device permanent. The token
    /// itself does not change - this is the same secret with a fresh Max-Age,
    /// so nothing is invalidated and there is no rotation window to race.
    /// </summary>
    private async Task RenewCookieIfStaleAsync(HttpContext context, IAuthService auth, EfAuthGrant grant, string? token, TimeProvider time)
    {
        if (token is null || time.GetUtcNow() - grant.CookieIssuedAt < options.GrantRenewAfter) return;

        AuthCookie.Issue(context.Response, options, token);
        await auth.MarkCookieIssuedAsync(grant, context.RequestAborted);
        logger.LogInformation("Re-issued the grant cookie for {GrantId} ({Label})", grant.Id, grant.Label);
    }

    /// <summary>
    /// Content-negotiated, for the reason spelled out on <see cref="AuthChallenge"/>:
    /// a 302 handed to a fetch is invisible to the code that made it.
    /// </summary>
    private void Refuse(HttpContext context)
    {
        var response = context.Response;

        // A cached refusal outlives the enrollment that fixes it, and the
        // symptom is a device that stays signed out until someone clears its
        // cache - which is not a thing anyone will guess.
        response.Headers.CacheControl = "no-store";

        if (AuthChallenge.PrefersRedirect(context.Request.Headers, context.Request.Method))
        {
            response.StatusCode = StatusCodes.Status302Found;
            response.Headers.Location = AuthChallenge.SignInLocation(options, context.Request.GetEncodedPathAndQuery());
            return;
        }

        response.StatusCode = StatusCodes.Status401Unauthorized;
    }
}

/// <summary>
/// How the rest of the app asks who is calling. Deliberately an
/// <see cref="HttpContext.Items"/> entry rather than a request header: a label
/// is free text an admin typed, and free text written into a header is a header
/// injection waiting to be found.
/// </summary>
public static class AuthContextExtensions
{
    private const string GrantKey = "aerie.auth.grant";

    public static void SetAuthGrant(this HttpContext context, EfAuthGrant grant) => context.Items[GrantKey] = grant;

    /// <summary>The grant the middleware authenticated, or null - which is also what every request looks like while Auth:Enabled is false.</summary>
    public static EfAuthGrant? GetAuthGrant(this HttpContext context) =>
        context.Items.TryGetValue(GrantKey, out var grant) ? grant as EfAuthGrant : null;
}

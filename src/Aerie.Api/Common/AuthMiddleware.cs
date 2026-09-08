using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Common;

/// <summary>
/// The wall, in-process. Traefik's forwardAuth is the outer gate and
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
/// also when Auth:EnforceInProcess is false - the canary, which needs Traefik
/// to be the only enforcer so the wall stands in front of exactly the routes
/// carrying its annotation.
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

        // The runner header, on the other hand, is only stripped where the wall
        // is up - it is local mode's whole way of naming itself, and removing it
        // above would delete it in exactly the mode it exists for. Under
        // gate.Enabled alone rather than the pair below, so the canary
        // (EnforceInProcess=false, wall up) strips it too.
        //
        // Belt and braces: CallerIdentity.LocalAsync also refuses to read this
        // unless Auth:Enabled is false, so neither the strip nor the check is
        // load-bearing on its own.
        if (gate.Enabled) context.Request.Headers.Remove(LocalCaller.RunnerHeader);

        // Two conditions, one no-op. Off is the rollback; on-but-not-enforcing
        // is the canary, where Traefik is deliberately the only
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
        //
        // Exempt is not the same as anonymous, though, and one path says so:
        // log shipping is let through unconditionally *and* wants a name on it
        // when there is one. See AuthGate.IdentifiesWithoutEnforcing for why
        // that distinction is a fix rather than a feature.
        if (gate.IsExempt(request.Path, host))
        {
            if (gate.IdentifiesWithoutEnforcing(request.Path))
            {
                await IdentifyAsync(context, auth, request);
            }

            await next(context);
            return;
        }

        var tokens = AuthCookie.ReadAll(request, options);
        var bearer = AuthBearer.Read(request);
        var clientIp = context.Connection.RemoteIpAddress?.ToString();
        var decision = await gate.EvaluateAsync(request.Path, host, tokens, bearer, clientIp, context.RequestAborted);

        if (decision.Outcome == AuthOutcome.Challenge)
        {
            Refuse(context, presentedKey: bearer is not null);
            return;
        }

        if (decision.Grant is { } grant)
        {
            context.SetAuthGrant(grant);
            await RenewCookieIfStaleAsync(context, auth, grant, decision.Token, time);
        }

        // No cookie re-issue on this lane, and nothing else either: a key is
        // the whole credential, held in a file the wall did not write and
        // cannot refresh.
        if (decision.ApiKey is { } key) context.SetApiKey(key);

        await next(context);
    }

    /// <summary>
    /// Names the caller on a request that was never going to be refused.
    ///
    /// Everything the enforcing path does *except* deciding anything: no
    /// challenge, and no cookie re-issue either - a request that does not have
    /// to present a credential is a poor place to renew one, and the sliding
    /// window belongs to the requests that actually go through the wall.
    ///
    /// Failure is silent by design. An unenrolled browser shipping logs is the
    /// ordinary case here (the sign-in shell itself does it), so "no grant" is
    /// an answer rather than an event.
    /// </summary>
    private async Task IdentifyAsync(HttpContext context, IAuthService auth, HttpRequest request)
    {
        var tokens = AuthCookie.ReadAll(request, options);
        if (tokens.Count == 0) return;

        var verified = await auth.VerifyAsync(tokens, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);
        if (verified is not null) context.SetAuthGrant(verified.Grant);
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
    /// <param name="presentedKey">
    /// Whether the caller sent a bearer key. One that did gets a 401 whatever
    /// its Accept header says: a program holding a key has no browser to send
    /// to a sign-in page, and a 302 into one would arrive as a 200 full of HTML
    /// - a refusal that looks like a success is the worst answer available.
    /// </param>
    private void Refuse(HttpContext context, bool presentedKey)
    {
        var response = context.Response;

        // A cached refusal outlives the enrollment that fixes it, and the
        // symptom is a device that stays signed out until someone clears its
        // cache - which is not a thing anyone will guess.
        response.Headers.CacheControl = "no-store";

        if (!presentedKey && AuthChallenge.PrefersRedirect(context.Request.Headers, context.Request.Method))
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
    private const string ApiKeyKey = "aerie.auth.apikey";

    public static void SetAuthGrant(this HttpContext context, EfAuthGrant grant) => context.Items[GrantKey] = grant;

    /// <summary>The grant the middleware authenticated, or null - which is also what every request looks like while Auth:Enabled is false.</summary>
    public static EfAuthGrant? GetAuthGrant(this HttpContext context) =>
        context.Items.TryGetValue(GrantKey, out var grant) ? grant as EfAuthGrant : null;

    public static void SetApiKey(this HttpContext context, EfApiKey key) => context.Items[ApiKeyKey] = key;

    /// <summary>
    /// The API key the middleware authenticated, or null. A request never has
    /// both this and a grant: the gate takes the bearer lane or the cookie
    /// lane, never one and then the other.
    /// </summary>
    public static EfApiKey? GetApiKey(this HttpContext context) =>
        context.Items.TryGetValue(ApiKeyKey, out var key) ? key as EfApiKey : null;
}

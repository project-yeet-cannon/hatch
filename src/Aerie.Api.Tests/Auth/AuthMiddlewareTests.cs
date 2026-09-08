using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Aerie.Api.Services.Media;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using System.Net;

namespace Aerie.Api.Tests.Auth;

/// <summary>
/// The in-process half of the wall: what a refused request actually looks like
/// on the wire, and what an authenticated one costs.
///
/// The content-negotiation cases carry the most weight. A 302 handed to a
/// `fetch` is invisible to the code that made it - the browser follows it, the
/// sign-in shell's HTML comes back 200, and the caller reports a JSON parse
/// error with nothing in it that mentions authentication.
/// </summary>
public class AuthMiddlewareTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WithTheGateOffTheRequestPassesThroughUntouched()
    {
        // Every phase before 5, and all of local dev.
        var (context, served) = await Run(Request("/api/zones"), enabled: false);

        Assert.True(served);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/media/Beatles/Revolver/01.flac")]
    [InlineData("/api/ui-logs")]
    [InlineData("/apps/auth/")]
    public async Task ExemptPathsAreServedAndNeverEvenReadTheCookie(string path)
    {
        var auth = new StubAuthService();

        var (_, served) = await Run(Request(path), auth: auth);

        Assert.True(served);
        Assert.Empty(auth.Verified);
    }

    [Fact]
    public async Task ANavigationWithoutAGrantBouncesToTheSignInShellCarryingWhereItWasGoing()
    {
        // The bit that makes a printed storage-bin QR one extra tap rather than
        // a dead end.
        var request = Request("/apps/family/storage/c/ABC123", query: "?ref=label");
        request.Headers["Sec-Fetch-Mode"] = "navigate";
        request.Headers.Accept = "text/html,application/xhtml+xml";

        var (context, served) = await Run(request);

        Assert.False(served);
        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal(
            "/apps/auth/?r=%2Fapps%2Ffamily%2Fstorage%2Fc%2FABC123%3Fref%3Dlabel",
            context.Response.Headers.Location.ToString());
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task AnOldBrowserThatOnlySaysAcceptStillGetsTheRedirect()
    {
        var request = Request("/apps/admin/devices");
        request.Headers.Accept = "text/html,*/*;q=0.8";

        var (context, _) = await Run(request);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
    }

    [Theory]
    // fetch() and XHR, which a 302 would swallow silently.
    [InlineData("cors", "*/*", null)]
    [InlineData("same-origin", "application/json", null)]
    [InlineData(null, "application/json", null)]
    [InlineData(null, "text/html", "XMLHttpRequest")]
    // A Sonos speaker reaching something outside the media prefix, and a probe.
    [InlineData(null, "audio/flac", null)]
    [InlineData(null, "", null)]
    public async Task EveryOtherCallerGetsABare401ItCanActuallySee(string? fetchMode, string accept, string? requestedWith)
    {
        var request = Request("/api/zones");
        if (fetchMode is not null) request.Headers["Sec-Fetch-Mode"] = fetchMode;
        if (requestedWith is not null) request.Headers["X-Requested-With"] = requestedWith;
        request.Headers.Accept = accept;

        var (context, served) = await Run(request);

        Assert.False(served);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }

    [Fact]
    public async Task ADocumentPostIsRefusedRatherThanRedirected()
    {
        // A form post that follows a 302 arrives at the sign-in shell with its
        // body dropped, which is a worse outcome than a 401 the caller can report.
        var request = Request("/api/zones");
        request.Method = HttpMethods.Post;
        request.Headers["Sec-Fetch-Mode"] = "navigate";

        var (context, _) = await Run(request);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task TheReturnUrlNeverBecomesAnAbsoluteOne()
    {
        // The sign-in shell is the page everybody is trained to trust, which
        // makes an open redirect through it the classic own-goal.
        var request = Request("//evil.example.com/");
        request.Headers["Sec-Fetch-Mode"] = "navigate";

        var (context, _) = await Run(request);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/apps/auth/", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task AGrantIsVerifiedFromTheCookieAndPublishedToTheRestOfTheRequest()
    {
        var grant = Grant();
        var auth = new StubAuthService(grant);
        var request = Request("/api/zones");
        request.Headers.Cookie = "aerie_grant=a-token";

        var (context, served) = await Run(request, auth: auth);

        Assert.True(served);
        Assert.Same(grant, context.GetAuthGrant());
        var (presented, ip) = Assert.Single(auth.Verified);
        Assert.Equal(["a-token"], presented);
        Assert.Equal("10.0.0.7", ip);
    }

    [Fact]
    public async Task AnInboundIdentityHeaderIsStrippedBeforeAnythingCanBelieveIt()
    {
        // Traefik sets these on the proxied request from its own
        // authResponseHeaders. A client that reaches a pod directly must not be
        // able to hand itself the same thing.
        var request = Request("/api/zones");
        request.Headers.Cookie = "aerie_grant=a-token";
        request.Headers[AuthChallenge.GrantHeader] = Guid.NewGuid().ToString();
        request.Headers[AuthChallenge.LabelHeader] = "Someone else's iPhone";

        var (context, served) = await Run(request, auth: new StubAuthService(Grant()));

        Assert.True(served);
        Assert.False(context.Request.Headers.ContainsKey(AuthChallenge.GrantHeader));
        Assert.False(context.Request.Headers.ContainsKey(AuthChallenge.LabelHeader));
    }

    /// <summary>
    /// The runner header is stripped under a wall, for the reason the grant
    /// header is: nothing downstream can tell a name a client sent from one it
    /// earned, and where there is a wall a name has to be earned.
    /// </summary>
    [Fact]
    public async Task AnInboundRunnerHeaderIsStrippedWhereTheWallIsUp()
    {
        var request = Request("/api/zones");
        request.Headers.Cookie = "aerie_grant=a-token";
        request.Headers[LocalCaller.RunnerHeader] = "host:/src";

        var (context, served) = await Run(request, auth: new StubAuthService(Grant()));

        Assert.True(served);
        Assert.False(context.Request.Headers.ContainsKey(LocalCaller.RunnerHeader));
    }

    /// <summary>
    /// And kept where it is down - which is the whole reason it is not stripped
    /// beside the grant header at the top of the middleware. That Remove runs
    /// before the wall-off return, so it would delete the header in exactly the
    /// mode it exists for.
    /// </summary>
    [Fact]
    public async Task TheRunnerHeaderSurvivesWhereTheWallIsOff()
    {
        var request = Request("/api/zones");
        request.Headers[LocalCaller.RunnerHeader] = "host:/src";

        var (context, served) = await Run(request, enabled: false);

        Assert.True(served);
        Assert.Equal("host:/src", context.Request.Headers[LocalCaller.RunnerHeader]);
    }

    /// <summary>
    /// The canary has the wall up and enforces nothing in-process, so it strips
    /// it too - which is why the check is under gate.Enabled alone rather than
    /// below the pair.
    /// </summary>
    [Fact]
    public async Task TheCanaryStripsTheRunnerHeaderAsWell()
    {
        var request = Request("/api/zones");
        request.Headers[LocalCaller.RunnerHeader] = "host:/src";

        var (context, served) = await Run(request, enforceInProcess: false);

        Assert.True(served);
        Assert.False(context.Request.Headers.ContainsKey(LocalCaller.RunnerHeader));
    }

    [Fact]
    public async Task AStaleCookieIsReIssuedWithTheSameTokenSoAnInUseDeviceNeverLapses()
    {
        var grant = Grant();
        var auth = new StubAuthService(grant);
        var request = Request("/api/zones");
        request.Headers.Cookie = "aerie_grant=a-token";

        // Chrome caps Max-Age at 400 days whatever we send, so the sliding
        // re-issue is the only thing that makes "until revoked" true.
        var (context, served) = await Run(request, auth: auth, at: Now.AddDays(31));

        Assert.True(served);
        // Two: the re-issued grant cookie, and the host-only tombstone that
        // travels with every issue (see AuthCookie.Issue).
        Assert.Equal(2, context.Response.Headers.SetCookie.Count);
        var cookie = context.Response.Headers.SetCookie[0]!;
        Assert.StartsWith("aerie_grant=a-token;", cookie);
        Assert.Contains("max-age=34560000", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("domain=.example.com", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([grant.Id], auth.CookieIssued);
    }

    [Fact]
    public async Task AFreshCookieIsLeftAloneSoTheHotPathStaysARead()
    {
        var auth = new StubAuthService(Grant());
        var request = Request("/api/zones");
        request.Headers.Cookie = "aerie_grant=a-token";

        var (context, _) = await Run(request, auth: auth, at: Now.AddDays(29));

        Assert.Equal(0, context.Response.Headers.SetCookie.Count);
        Assert.Empty(auth.CookieIssued);
    }

    [Fact]
    public async Task ARevokedGrantIsRefusedEvenThoughItPresentedACookie()
    {
        var request = Request("/api/zones");
        request.Headers.Cookie = "aerie_grant=a-revoked-token";

        var (context, served) = await Run(request, auth: new StubAuthService(null));

        Assert.False(served);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task DuringTheCanaryThePodServesWhatItWouldOtherwiseRefuse()
    {
        // The canary: Auth:Enabled is on so /api/auth/verify decides for real on
        // Traefik's behalf, but this pod enforces nothing itself, which is what
        // confines the wall to the routes carrying the annotation. The honest
        // consequence is exactly this - a request that reaches the pod without
        // going through Traefik is served.
        var auth = new StubAuthService();

        var (context, served) = await Run(Request("/api/zones"), enforceInProcess: false, auth: auth);

        Assert.True(served);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Empty(auth.Verified);
    }

    [Fact]
    public async Task TheCanaryStillStripsAForgedIdentityHeader()
    {
        // The one thing that must not be suspended with the rest. Nothing
        // downstream can distinguish a header Traefik set from one a client
        // sent, and during the canary a client can reach the pod directly.
        var request = Request("/api/zones");
        request.Headers[AuthChallenge.GrantHeader] = Guid.NewGuid().ToString();
        request.Headers[AuthChallenge.LabelHeader] = "Someone else's iPhone";

        var (context, served) = await Run(request, enforceInProcess: false);

        Assert.True(served);
        Assert.False(context.Request.Headers.ContainsKey(AuthChallenge.GrantHeader));
        Assert.False(context.Request.Headers.ContainsKey(AuthChallenge.LabelHeader));
    }

    [Fact]
    public async Task NamesTheCallerOnLogShippingEvenThoughItIsExempt()
    {
        // The bug this fixes: /api/ui-logs is on the allow-list, so the
        // middleware used to return before ever attaching a grant - and
        // UiLogsController's `actor` field, which reads exactly that grant, was
        // silently null on every line the app had ever shipped. Exempt from
        // refusal is not the same as exempt from being looked at.
        var grant = Grant();
        var auth = new StubAuthService(grant);
        var request = Request("/api/ui-logs");
        request.Headers.Cookie = "aerie_grant=a-token";

        var (context, served) = await Run(request, auth: auth);

        Assert.True(served);
        Assert.Same(grant, context.GetAuthGrant());
    }

    [Fact]
    public async Task ShipsLogsForABrowserThatHasNoGrantAtAll()
    {
        // The ordinary case, and the reason identification is silent about
        // failing: the sign-in shell ships logs too, and it is by definition
        // holding nothing. A gated log endpoint hides exactly the failures
        // worth seeing.
        var request = Request("/api/ui-logs");

        var (context, served) = await Run(request, auth: new StubAuthService());

        Assert.True(served);
        Assert.Null(context.GetAuthGrant());
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/media/Beatles/Revolver/01.flac")]
    public async Task DoesNotSpendACredentialLookupOnTheExemptPathsThatMustCostNothing(string path)
    {
        // Identification is one entry on a list, not a blanket. The health
        // probes fire on every pod forever and Sonos streams every byte of
        // every song through here; a database round trip on either is the
        // reason the allow-list exists in the first place.
        var auth = new StubAuthService(Grant());
        var request = Request(path);
        request.Headers.Cookie = "aerie_grant=a-token";

        var (context, served) = await Run(request, auth: auth);

        Assert.True(served);
        Assert.Empty(auth.Verified);
        Assert.Null(context.GetAuthGrant());
    }

    [Fact]
    public async Task DoesNotRenewACookieOnARequestThatNeverHadToPresentOne()
    {
        // The sliding window belongs to requests that actually go through the
        // wall. Renewing here would mean a browser that only ever ships logs
        // keeps itself signed in forever without ever being asked for anything.
        var grant = Grant();
        grant.CookieIssuedAt = Now.AddDays(-400);
        var auth = new StubAuthService(grant);
        var request = Request("/api/ui-logs");
        request.Headers.Cookie = "aerie_grant=a-token";

        var (context, _) = await Run(request, auth: auth);

        Assert.Empty(auth.CookieIssued);
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
    }

    private static EfAuthGrant Grant() => new()
    {
        Id = Guid.NewGuid(),
        TokenHash = AuthTokens.Hash("a-token"),
        Label = "Kitchen tablet",
        Kind = AuthGrantKind.Device,
        CreatedAt = Now,
        CookieIssuedAt = Now,
    };

    private static HttpRequest Request(string path, string? query = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.7");
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("home.example.com");
        context.Request.Path = path;
        if (query is not null) context.Request.QueryString = new QueryString(query);
        return context.Request;
    }

    private static async Task<(HttpContext Context, bool Served)> Run(
        HttpRequest request,
        bool enabled = true,
        bool enforceInProcess = true,
        IAuthService? auth = null,
        DateTimeOffset? at = null)
    {
        var options = Options.Create(new AuthOptions
        {
            Enabled = enabled,
            EnforceInProcess = enforceInProcess,
            CookieName = "aerie_grant",
            CookieDomain = ".example.com",
        });

        var gate = new AuthGate(
            auth ??= new StubAuthService(),
            options,
            Options.Create(new MediaLibraryOptions { RequestPath = "/media" }),
            NullLogger<AuthGate>.Instance);

        var served = false;
        var middleware = new AuthMiddleware(
            _ =>
            {
                served = true;
                return Task.CompletedTask;
            },
            options,
            NullLogger<AuthMiddleware>.Instance);

        await middleware.InvokeAsync(request.HttpContext, gate, auth, new FakeTimeProvider(at ?? Now));

        return (request.HttpContext, served);
    }
}

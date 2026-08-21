using Aerie.Api.Common;
using Aerie.Api.Controllers;
using Aerie.Api.Ef;
using Aerie.Api.Models.Auth;
using Aerie.Api.Services.Auth;
using Aerie.Api.Services.Media;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;

namespace Aerie.Api.Tests.Auth;

/// <summary>
/// The forwardAuth endpoint and the three endpoints a device uses on itself.
///
/// Verify carries the load here. Traefik proxies a *copy* of the original
/// request to it, so the request this controller can see is always GET
/// /api/auth/verify on this pod - a path that is itself on the allow-list.
/// Every case below is really one question: does the endpoint answer about the
/// request Traefik asked about, or about itself?
/// </summary>
public class AuthControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/media/Beatles/Revolver/01.flac")]
    [InlineData("/api/ui-logs")]
    [InlineData("/apps/auth/r/K3M9P2QT")]
    public async Task VerifyAllowsAnExemptOriginalPath(string uri)
    {
        var controller = NewController(out var context);
        Forwarded(context, uri);

        Assert.IsType<NoContentResult>(await controller.Verify(CancellationToken.None));
    }

    [Fact]
    public async Task VerifyFailsClosedWhenTraefikDidNotSayWhatItIsAskingAbout()
    {
        // The trap: this request's own path is /api/auth/verify, which is
        // exempt. Falling back to it would answer 204 to everything the moment
        // the forwardAuth headers stopped arriving - a gate that opens when it
        // breaks, which is worse than no gate because it looks like one.
        var controller = NewController(out var context);

        Assert.IsType<UnauthorizedResult>(await controller.Verify(CancellationToken.None));
    }

    [Theory]
    // Both spellings of the traversal, because Traefik forwards the raw URI and
    // Kestrel will have collapsed it by the time anything is served.
    [InlineData("/media/../apps/admin")]
    [InlineData("/media/%2e%2e/apps/admin")]
    [InlineData("/media/./../apps/admin")]
    public async Task VerifyJudgesThePathTheOriginServerWillActuallyServe(string uri)
    {
        var controller = NewController(out var context);
        Forwarded(context, uri);

        Assert.IsType<UnauthorizedResult>(await controller.Verify(CancellationToken.None));
    }

    [Fact]
    public async Task VerifyRedirectsANavigationAndCarriesTheOriginalUrl()
    {
        var controller = NewController(out var context);
        Forwarded(context, "/apps/admin/devices?tab=zones");
        context.Request.Headers["Sec-Fetch-Mode"] = "navigate";

        var result = Assert.IsType<StatusCodeResult>(await controller.Verify(CancellationToken.None));

        Assert.Equal(StatusCodes.Status302Found, result.StatusCode);
        // Relative, so the browser resolves it against whichever host it was
        // going to - kiosk. stays on kiosk. - and ?r= never carries an
        // absolute URL anywhere near the sign-in shell.
        Assert.Equal(
            "/apps/auth/?r=%2Fapps%2Fadmin%2Fdevices%3Ftab%3Dzones",
            context.Response.Headers.Location.ToString());
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task VerifyRefusesAFetchWithABare401()
    {
        var controller = NewController(out var context);
        Forwarded(context, "/api/zones");
        context.Request.Headers["Sec-Fetch-Mode"] = "cors";
        context.Request.Headers.Accept = "*/*";

        Assert.IsType<UnauthorizedResult>(await controller.Verify(CancellationToken.None));
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }

    [Fact]
    public async Task VerifyReadsTheMethodTraefikForwardedRatherThanItsOwnGet()
    {
        // The proxied request is always a GET, so a POST that must not be
        // bounced is only distinguishable from X-Forwarded-Method.
        var controller = NewController(out var context);
        Forwarded(context, "/api/zones", method: HttpMethods.Post);
        context.Request.Headers["Sec-Fetch-Mode"] = "navigate";

        Assert.IsType<UnauthorizedResult>(await controller.Verify(CancellationToken.None));
    }

    [Fact]
    public async Task VerifyNamesTheGrantForTraefikToCopyOnward()
    {
        var grant = Grant();
        var controller = NewController(out var context, new StubAuthService(grant));
        Forwarded(context, "/apps/admin/devices");
        context.Request.Headers.Cookie = "aerie_grant=a-token";

        Assert.IsType<NoContentResult>(await controller.Verify(CancellationToken.None));
        Assert.Equal(grant.Id.ToString(), context.Response.Headers[AuthChallenge.GrantHeader].ToString());
        Assert.Equal("Kitchen tablet", context.Response.Headers[AuthChallenge.LabelHeader].ToString());
    }

    [Fact]
    public async Task VerifyKeepsAnAwkwardLabelFromBecomingASecondHeaderLine()
    {
        // A label is free text an admin typed, and this one is about to become
        // a response header.
        var controller = NewController(out var context, new StubAuthService(Grant("Ada\r\nX-Admin: yes")));
        Forwarded(context, "/apps/admin/devices");
        context.Request.Headers.Cookie = "aerie_grant=a-token";

        await controller.Verify(CancellationToken.None);

        Assert.Equal("Ada??X-Admin: yes", context.Response.Headers[AuthChallenge.LabelHeader].ToString());
    }

    [Fact]
    public async Task VerifyUsesTheForwardedHostSoAnExemptHostStaysExempt()
    {
        var controller = NewController(out var context, exemptHosts: ["files.example.com"]);
        Forwarded(context, "/aerie-kiosk.apk", host: "files.example.com");

        Assert.IsType<NoContentResult>(await controller.Verify(CancellationToken.None));
    }

    [Fact]
    public async Task RedeemMintsAGrantAndHandsBackTheCookie()
    {
        var grant = Grant();
        var auth = new StubAuthService { RedeemResult = new AuthRedemption(grant, "a-token", null) };
        var controller = NewController(out var context, auth);
        context.Request.Headers.UserAgent = "Mozilla/5.0 (iPhone)";

        var result = Assert.IsType<OkObjectResult>(
            await controller.Redeem(new RedeemRequest("AERIE-K3M9-P2QT", "Ada's iPhone"), CancellationToken.None));

        var dto = Assert.IsType<AuthGrantDto>(result.Value);
        Assert.Equal(grant.Id, dto.Id);
        Assert.True(dto.IsCurrent);
        Assert.Equal([("AERIE-K3M9-P2QT", "Ada's iPhone", "Mozilla/5.0 (iPhone)", "10.0.0.7")], auth.Redemptions);

        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith("aerie_grant=a-token;", cookie);
        Assert.Contains("domain=.example.com", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AuthRedemption.InvalidCode)]
    [InlineData(AuthRedemption.Expired)]
    [InlineData(AuthRedemption.AlreadyRedeemed)]
    public async Task ARefusedRedemptionSaysWhichRefusalItWasAndSetsNoCookie(string error)
    {
        // "That code expired" and "that isn't a code" send a person to
        // different next actions, and neither tells an attacker anything a
        // 15-minute single-use code hadn't already conceded.
        var auth = new StubAuthService { RedeemResult = AuthRedemption.Failed(error) };
        var controller = NewController(out var context, auth);

        var result = Assert.IsType<BadRequestObjectResult>(
            await controller.Redeem(new RedeemRequest("AERIE-0000-0000", null), CancellationToken.None));

        Assert.Equal(error, Assert.IsType<AuthErrorDto>(result.Value).Error);
        Assert.Equal(0, context.Response.Headers.SetCookie.Count);
    }

    [Fact]
    public async Task MeAnswersFromTheCookieWhenTheWallIsDown()
    {
        // Every phase before 5, and all of local dev: the middleware never
        // looked, so "who am I" has to look for itself.
        var auth = new StubAuthService(Grant());
        var controller = NewController(out var context, auth, enabled: false);
        context.Request.Headers.Cookie = "aerie_grant=a-token";

        var result = Assert.IsType<OkObjectResult>(await controller.Me(CancellationToken.None));

        Assert.Equal("Kitchen tablet", Assert.IsType<AuthGrantDto>(result.Value).Label);
        Assert.Equal([("a-token", "10.0.0.7")], auth.Verified);
    }

    [Fact]
    public async Task MeReusesWhatTheMiddlewareAlreadyResolvedRatherThanVerifyingTwice()
    {
        var grant = Grant();
        var auth = new StubAuthService(grant);
        var controller = NewController(out var context, auth);
        context.SetAuthGrant(grant);

        Assert.IsType<OkObjectResult>(await controller.Me(CancellationToken.None));
        Assert.Empty(auth.Verified);
    }

    [Fact]
    public async Task MeRefusesAnUnenrolledCaller()
    {
        var controller = NewController(out _, new StubAuthService(null));

        Assert.IsType<UnauthorizedResult>(await controller.Me(CancellationToken.None));
    }

    [Fact]
    public async Task SigningOutDeletesTheGrantAndExpiresTheCookie()
    {
        var grant = Grant();
        var auth = new StubAuthService(grant);
        var controller = NewController(out var context, auth);
        context.Request.Headers.Cookie = "aerie_grant=a-token";

        Assert.IsType<NoContentResult>(await controller.SignOutDevice(CancellationToken.None));

        // Revocation is deletion - the same operation the Sessions page
        // performs on someone else's device, so there is no second notion of a
        // "signed out but still enrolled" grant to keep consistent.
        Assert.Equal([grant.Id], auth.Revoked);
        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith("aerie_grant=;", cookie);
        Assert.Contains("expires=Thu, 01 Jan 1970", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("domain=.example.com", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SigningOutWithNothingToSignOutOfStillClearsTheCookie()
    {
        // A cookie holding a token no row answers to is exactly the state that
        // makes "sign out and try again" fail to fix anything.
        var auth = new StubAuthService(null);
        var controller = NewController(out var context, auth);
        context.Request.Headers.Cookie = "aerie_grant=a-stale-token";

        Assert.IsType<NoContentResult>(await controller.SignOutDevice(CancellationToken.None));

        Assert.Empty(auth.Revoked);
        Assert.StartsWith("aerie_grant=;", context.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task TheSessionListMarksTheCallersOwnDeviceAndOnlyThat()
    {
        var mine = Grant("This laptop");
        var theirs = Grant("Kitchen tablet");
        var auth = new StubAuthService(mine);
        auth.Grants.AddRange([theirs, mine]);
        var controller = NewController(out var context, auth);
        context.Request.Headers.Cookie = "aerie_grant=a-token";

        var result = Assert.IsType<OkObjectResult>(await controller.ListGrants(CancellationToken.None));

        var dtos = Assert.IsType<List<AuthGrantDto>>(result.Value);
        Assert.Equal(["Kitchen tablet", "This laptop"], dtos.Select(d => d.Label));
        Assert.Equal([false, true], dtos.Select(d => d.IsCurrent));
    }

    [Fact]
    public async Task TheSessionListMarksNothingCurrentWhenTheCallerHoldsNoGrant()
    {
        // Every phase before 5 and all of local dev: the wall is down, so the
        // page is reachable without a grant and no row is "this device".
        var auth = new StubAuthService(null);
        auth.Grants.Add(Grant());
        var controller = NewController(out _, auth, enabled: false);

        var result = Assert.IsType<OkObjectResult>(await controller.ListGrants(CancellationToken.None));

        Assert.False(Assert.Single(Assert.IsType<List<AuthGrantDto>>(result.Value)).IsCurrent);
    }

    [Fact]
    public async Task RevokingAnotherDeviceDeletesIt()
    {
        var auth = new StubAuthService(Grant());
        var controller = NewController(out var context, auth);
        context.Request.Headers.Cookie = "aerie_grant=a-token";
        var theirs = Guid.NewGuid();

        Assert.IsType<NoContentResult>(await controller.RevokeGrant(theirs, CancellationToken.None));
        Assert.Equal([theirs], auth.Revoked);
    }

    [Fact]
    public async Task RevokingYourOwnDeviceIsRefusedRatherThanLeavingACookieNoRowAnswersTo()
    {
        // Not squeamishness about lockout - it is that this path deletes the
        // row and cannot clear the cookie on the browser it isn't answering,
        // which is the state that makes "sign out and back in" fail to help.
        var mine = Grant();
        var auth = new StubAuthService(mine);
        var controller = NewController(out var context, auth);
        context.Request.Headers.Cookie = "aerie_grant=a-token";

        var result = Assert.IsType<BadRequestObjectResult>(await controller.RevokeGrant(mine.Id, CancellationToken.None));

        Assert.Equal(AuthController.OwnGrantError, Assert.IsType<AuthErrorDto>(result.Value).Error);
        Assert.Empty(auth.Revoked);
    }

    [Fact]
    public async Task RevokingAGrantThatIsAlreadyGoneIsA404()
    {
        var auth = new StubAuthService(Grant()) { RevokeResult = false };
        var controller = NewController(out var context, auth);
        context.Request.Headers.Cookie = "aerie_grant=a-token";

        Assert.IsType<NotFoundResult>(await controller.RevokeGrant(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task AnInviteComesBackOnceWithEverythingAQrAndAReadingVoiceNeed()
    {
        var auth = new StubAuthService(Grant());
        var controller = NewController(out var context, auth);

        var result = Assert.IsType<OkObjectResult>(
            await controller.CreateInvite(new CreateInviteRequest("Ada's iPhone"), CancellationToken.None));

        var dto = Assert.IsType<AuthInviteDto>(result.Value);
        Assert.Equal("K3M9P2QT", dto.Code);
        Assert.Equal("AERIE-K3M9-P2QT", dto.FormattedCode);
        // Built from Auth:SignInPath, so an install that mounts the shell
        // elsewhere moves the QR target with it.
        Assert.Equal("/apps/auth/r/K3M9P2QT", dto.RedeemPath);
        Assert.Equal("Ada's iPhone", dto.Label);
        Assert.Equal([("Ada's iPhone", false)], auth.InvitesCreated);
        // A live credential has no business in a cache, anyone's.
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    private static EfAuthGrant Grant(string label = "Kitchen tablet") => new()
    {
        Id = Guid.NewGuid(),
        TokenHash = AuthTokens.Hash("a-token"),
        Label = label,
        Kind = AuthGrantKind.Interactive,
        CreatedAt = Now,
        CookieIssuedAt = Now,
    };

    /// <summary>The headers Traefik's forwardAuth adds; everything else on the request is the original's, unchanged.</summary>
    private static void Forwarded(HttpContext context, string uri, string? method = null, string host = "home.example.com")
    {
        context.Request.Headers["X-Forwarded-Method"] = method ?? HttpMethods.Get;
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = host;
        context.Request.Headers["X-Forwarded-Uri"] = uri;
    }

    private static AuthController NewController(
        out HttpContext context,
        IAuthService? auth = null,
        bool enabled = true,
        string[]? exemptHosts = null)
    {
        var options = Options.Create(new AuthOptions
        {
            Enabled = enabled,
            CookieName = "aerie_grant",
            CookieDomain = ".example.com",
            ExemptHosts = exemptHosts ?? [],
        });

        auth ??= new StubAuthService();
        var gate = new AuthGate(
            auth,
            options,
            Options.Create(new MediaLibraryOptions { RequestPath = "/media" }),
            NullLogger<AuthGate>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.7");
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("api.aerie.svc.cluster.local");
        httpContext.Request.Path = "/api/auth/verify";
        context = httpContext;

        return new AuthController(gate, auth, options, NullLogger<AuthController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}

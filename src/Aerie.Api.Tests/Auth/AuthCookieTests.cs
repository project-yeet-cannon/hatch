using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Http;

namespace Aerie.Api.Tests.Auth;

/// <summary>
/// The cookie layer, and specifically the case that took the house down on
/// 2026-08-22: a browser presenting two cookies of the same name.
///
/// Cookie identity is (name, domain, path), so a host-only `home.example.com`
/// cookie and a domain-wide `.example.com` one are two different cookies that
/// arrive together in one header. Reading only one of them is how a device that
/// holds a perfectly good grant gets refused `unknown_grant` on exactly one
/// host - and, because every fresh sign-in adds another cookie that is then
/// ignored, a sign-in loop with no exit.
/// </summary>
public class AuthCookieTests
{
    private static AuthOptions Options(string cookieDomain = ".example.com") =>
        new() { CookieName = "__Secure-aerie_grant", CookieDomain = cookieDomain };

    private static HttpRequest RequestWith(string cookieHeader)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = cookieHeader;
        return context.Request;
    }

    [Fact]
    public void ReadAllKeepsEveryCookieOfTheName()
    {
        // HttpRequest.Cookies is a dictionary and collapses these to one, which
        // is the whole reason ReadAll parses the header itself.
        var request = RequestWith("__Secure-aerie_grant=stale; other=x; __Secure-aerie_grant=good");

        Assert.Equal(["stale", "good"], AuthCookie.ReadAll(request, Options()));
    }

    [Fact]
    public void ReadAllIgnoresBlanksAndOtherNames()
    {
        // The blank is a tombstone in flight - a cleared cookie arrives as a
        // present-but-empty value, and it is not a credential.
        var request = RequestWith("__Secure-aerie_grant=; session=abc; __Secure-aerie_grant=good");

        Assert.Equal(["good"], AuthCookie.ReadAll(request, Options()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("other=x")]
    public void ReadAllFindsNothingWhenNothingWasPresented(string header)
    {
        Assert.Empty(AuthCookie.ReadAll(RequestWith(header), Options()));
    }

    [Fact]
    public async Task AStaleCookieInFrontOfAGoodOneDoesNotLockTheDeviceOut()
    {
        // The live failure, end to end: the phone's stale host-only cookie is
        // sent first, the grant it names is long deleted, and the good
        // domain-wide cookie is right behind it. Refusing here is what made
        // home.example.com unreachable while kiosk.example.com worked.
        var grant = new EfAuthGrant
        {
            TokenHash = AuthTokens.Hash("good"),
            Label = "Nathan's iPhone",
            Kind = AuthGrantKind.Device,
            CreatedAt = DateTimeOffset.UnixEpoch,
            CookieIssuedAt = DateTimeOffset.UnixEpoch,
        };
        var auth = new StubAuthService(grant) { VerifiesToken = "good" };
        var gate = new AuthGate(
            auth,
            Microsoft.Extensions.Options.Options.Create(new AuthOptions { Enabled = true, CookieDomain = ".example.com" }),
            Microsoft.Extensions.Options.Options.Create(new Aerie.Api.Services.Media.MediaLibraryOptions { RequestPath = "/media" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthGate>.Instance);

        var decision = await gate.EvaluateAsync("/apps/family/", "home.example.com", ["stale", "good"], "10.0.0.7", CancellationToken.None);

        Assert.Equal(AuthOutcome.Authenticated, decision.Outcome);
        Assert.Same(grant, decision.Grant);
        // And the re-issue writes back the cookie that actually verified, not
        // whichever one happened to be first.
        Assert.Equal("good", decision.Token);
    }

    [Fact]
    public void IssueSendsTheGrantCookieAndATombstoneForTheHostOnlyOne()
    {
        var context = new DefaultHttpContext();

        AuthCookie.Issue(context.Response, Options(), "a-token");

        var headers = context.Response.Headers.SetCookie;
        Assert.Equal(2, headers.Count);
        Assert.StartsWith("__Secure-aerie_grant=a-token;", headers[0]!);
        Assert.Contains("domain=.example.com", headers[0]!, StringComparison.OrdinalIgnoreCase);

        Assert.StartsWith("__Secure-aerie_grant=;", headers[1]!);
        Assert.DoesNotContain("domain=", headers[1]!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IssueSendsNoTombstoneWhenTheCookieIsHostOnlyByDesign()
    {
        // Local dev has no cookie domain, so the cookie being issued *is* the
        // host-only one - a tombstone here would delete what was just set.
        var context = new DefaultHttpContext();

        AuthCookie.Issue(context.Response, Options(cookieDomain: ""), "a-token");

        var only = Assert.Single(context.Response.Headers.SetCookie.ToArray());
        Assert.StartsWith("__Secure-aerie_grant=a-token;", only!);
    }
}

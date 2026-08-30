using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Net;

namespace Aerie.Api.Tests.Auth;

/// <summary>
/// The one place anything outside Services/Auth asks who is calling. Three
/// properties carry it, and each one is a bug somewhere else if it breaks: the
/// middleware's answer is preferred when there is one, the cookie is still read
/// when the wall is off, and a device nobody has claimed reports the same
/// nobody as a browser that never enrolled.
/// </summary>
public class CallerIdentityTests
{
    [Fact]
    public async Task Grant_PrefersTheOneTheMiddlewareAlreadyResolved()
    {
        var attached = NewGrant();
        var auth = new StubAuthService(NewGrant());
        var context = NewContext();
        context.SetAuthGrant(attached);

        var resolved = await NewIdentity(context, auth).GrantAsync(default);

        Assert.Same(attached, resolved);
        // Not a micro-optimization: VerifyAsync writes LastSeenAt, so a second
        // lookup per request would double the write on the hottest path.
        Assert.Empty(auth.Verified);
    }

    /// <summary>
    /// Auth:Enabled is false in local development and under AUTH_MODE=none, so
    /// AuthMiddleware never runs and never attaches anything - while the
    /// browser is still holding a perfectly good cookie. Without this fallback
    /// every person-scoped feature would be dark on a developer's machine.
    /// </summary>
    [Fact]
    public async Task Grant_ReadsTheCookieWhenTheWallIsOff()
    {
        var grant = NewGrant();
        var auth = new StubAuthService(grant);
        var context = NewContext();
        context.Request.Headers.Cookie = "aerie_grant=tok";

        var resolved = await NewIdentity(context, auth).GrantAsync(default);

        Assert.Same(grant, resolved);
        Assert.Equal("10.0.0.7", Assert.Single(auth.Verified).ClientIp);
    }

    [Fact]
    public async Task Grant_ResolvesOncePerRequestHoweverOftenItIsAsked()
    {
        var auth = new StubAuthService(NewGrant());
        var context = NewContext();
        context.Request.Headers.Cookie = "aerie_grant=tok";
        var identity = NewIdentity(context, auth);

        await identity.GrantAsync(default);
        await identity.PersonIdAsync(default);
        await identity.GrantAsync(default);

        Assert.Single(auth.Verified);
    }

    /// <summary>A miss is remembered too, or an unenrolled browser pays for a lookup per question.</summary>
    [Fact]
    public async Task Grant_RemembersThatThereWasNobody()
    {
        var auth = new StubAuthService();
        var context = NewContext();
        var identity = NewIdentity(context, auth);

        Assert.Null(await identity.GrantAsync(default));
        Assert.Null(await identity.GrantAsync(default));
        Assert.Single(auth.Verified);
    }

    [Fact]
    public async Task PersonId_IsTheGrantsOwner()
    {
        var personId = Guid.NewGuid();
        var context = NewContext();
        context.SetAuthGrant(NewGrant(personId));

        Assert.Equal(personId, await NewIdentity(context, new StubAuthService()).PersonIdAsync(default));
    }

    /// <summary>
    /// The two nulls are one answer on purpose. A caller that needs a person has
    /// nobody either way, and telling "this device is unclaimed" apart from
    /// "this browser never enrolled" is how one refusal becomes two that leak
    /// which of them happened.
    /// </summary>
    [Fact]
    public async Task PersonId_IsNullForAnUnclaimedDevice_AndForNoDeviceAtAll()
    {
        var unclaimed = NewContext();
        unclaimed.SetAuthGrant(NewGrant(personId: null));

        Assert.Null(await NewIdentity(unclaimed, new StubAuthService()).PersonIdAsync(default));
        Assert.Null(await NewIdentity(NewContext(), new StubAuthService()).PersonIdAsync(default));
    }

    /// <summary>
    /// A hosted service or a Quartz job has no request behind it. Null rather
    /// than a throw: "nobody is calling" is a true answer, and background work
    /// that asks should read like every other unauthenticated caller.
    /// </summary>
    [Fact]
    public async Task Grant_IsNullOutsideARequest()
    {
        var identity = new CallerIdentity(new HttpContextAccessor(), new StubAuthService(NewGrant()), NewOptions());

        Assert.Null(await identity.GrantAsync(default));
    }

    private static EfAuthGrant NewGrant(Guid? personId = null) => new()
    {
        Id = Guid.NewGuid(),
        TokenHash = new byte[AuthHash.Length],
        Label = "Ada's iPhone",
        Kind = AuthGrantKind.Interactive,
        CreatedAt = DateTimeOffset.UnixEpoch,
        CookieIssuedAt = DateTimeOffset.UnixEpoch,
        PersonId = personId,
    };

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.7");
        return context;
    }

    private static CallerIdentity NewIdentity(HttpContext context, IAuthService auth) =>
        new(new HttpContextAccessor { HttpContext = context }, auth, NewOptions());

    private static IOptions<AuthOptions> NewOptions() =>
        Options.Create(new AuthOptions { Enabled = false, CookieName = "aerie_grant" });
}

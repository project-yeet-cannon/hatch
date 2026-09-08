using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Net;

namespace Aerie.Api.Tests.Auth;

/// <summary>
/// The one place anything outside Services/Auth asks who is calling. Four
/// properties carry it, and each one is a bug somewhere else if it breaks: the
/// middleware's answer is preferred when there is one, the cookie is still read
/// when the wall is off, a device nobody has claimed reports the same nobody as
/// a browser that never enrolled, and the person comes back as the row the wall
/// already loaded rather than as a second query.
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
    /// The row, not the key - the one question that needs a column off the
    /// person rather than something to compare a foreign key against, which is
    /// AdminGate reading IsAdmin.
    /// </summary>
    [Fact]
    public async Task Person_IsTheRowTheWallAlreadyLoaded()
    {
        var person = new EfPerson
        {
            Id = Guid.NewGuid(),
            Name = "Ada",
            IsAdmin = true,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        var grant = NewGrant(person.Id);
        grant.Person = person;

        var context = NewContext();
        context.SetAuthGrant(grant);

        Assert.Same(person, await NewIdentity(context, new StubAuthService()).PersonAsync(default));
    }

    /// <summary>
    /// No second query, ever. This reads the navigation property, which is what
    /// makes AuthService.VerifyAsync's Include load-bearing - and it is the
    /// reason a grant assembled without that include reports "nobody" rather
    /// than quietly issuing a join on the hottest path in the app.
    /// </summary>
    [Fact]
    public async Task Person_IsNullWhenNobodyHoldsTheDevice_AndWhenNoDeviceHeldIt()
    {
        var unclaimed = NewContext();
        unclaimed.SetAuthGrant(NewGrant(personId: null));

        Assert.Null(await NewIdentity(unclaimed, new StubAuthService()).PersonAsync(default));
        Assert.Null(await NewIdentity(NewContext(), new StubAuthService()).PersonAsync(default));
    }

    /// <summary>
    /// A hosted service or a Quartz job has no request behind it. Null rather
    /// than a throw: "nobody is calling" is a true answer, and background work
    /// that asks should read like every other unauthenticated caller.
    /// </summary>
    [Fact]
    public async Task Grant_IsNullOutsideARequest()
    {
        var identity = new CallerIdentity(
            new HttpContextAccessor(), new StubAuthService(NewGrant()), NewOptions(), new StubSiteSettings());

        Assert.Null(await identity.GrantAsync(default));
    }

    // ---- The third lane: who is calling when there is no wall ----

    /// <summary>
    /// Nothing presented, wall off: whoever started the app. This is the case
    /// the whole lane exists for - every event a browser writes against a local
    /// install used to read "operator".
    /// </summary>
    [Fact]
    public async Task WithTheWallOff_ACallerWithNothingAtAll_IsTheLocalPerson()
    {
        var identity = NewIdentity(
            NewContext(),
            new StubAuthService(),
            new AuthOptions { Enabled = false, LocalPerson = { Name = "Ada" } });

        var local = await identity.LocalAsync(default);

        Assert.Equal(new Actor(ActorKind.Person, LocalCaller.PersonId, "Ada"), local);
        Assert.Equal("Ada", await identity.ActorNameAsync(default));
        Assert.False(await identity.IsProgramAsync(default));
    }

    /// <summary>The setting beats the value the app was started with, which is what makes AERIE-936 a page rather than a restart.</summary>
    [Fact]
    public async Task TheSiteSetting_OutranksTheConfiguredName()
    {
        var identity = NewIdentity(
            NewContext(),
            new StubAuthService(),
            new AuthOptions { Enabled = false, LocalPerson = { Name = "Ada" } },
            new StubSiteSettings(localPersonName: "Grace"));

        Assert.Equal("Grace", (await identity.LocalAsync(default))?.Name);
    }

    [Fact]
    public async Task WithNothingConfiguredAnywhere_ThereIsStillAName()
    {
        var identity = NewIdentity(NewContext(), new StubAuthService());

        Assert.Equal(LocalCaller.DefaultName, await identity.ActorNameAsync(default));
    }

    /// <summary>
    /// A process that went to the trouble of naming itself is telling the truth
    /// about not being the person at the keyboard - so the header wins, and the
    /// caller reads as a program.
    /// </summary>
    [Fact]
    public async Task WithTheWallOff_TheRunnerHeaderNamesAProgram()
    {
        var context = NewContext();
        context.Request.Headers[LocalCaller.RunnerHeader] = "host:/src";

        var identity = NewIdentity(context, new StubAuthService());
        var local = await identity.LocalAsync(default);

        Assert.Equal(new Actor(ActorKind.Key, LocalCaller.RunnerIdFor("host:/src"), "host:/src"), local);
        Assert.Equal("host:/src", await identity.ActorNameAsync(default));
        Assert.True(await identity.IsProgramAsync(default));
    }

    /// <summary>A key is the more specific answer, and it is a row - the header does not get to shadow one.</summary>
    [Fact]
    public async Task ABearerKey_OutranksTheRunnerHeader()
    {
        var key = new EfApiKey
        {
            Id = Guid.NewGuid(),
            Name = "Claude",
            Prefix = "aerie_ak_abc",
            Hash = AuthTokens.Hash("aerie_ak_secret"),
            Scopes = [ApiKeyScopes.Hatch],
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

        var context = NewContext();
        context.Request.Headers[LocalCaller.RunnerHeader] = "host:/src";
        context.SetApiKey(key);

        var identity = NewIdentity(context, new StubAuthService());

        Assert.Null(await identity.LocalAsync(default));
        Assert.Equal("Claude", await identity.ActorNameAsync(default));
        Assert.True(await identity.IsProgramAsync(default));
    }

    /// <summary>A browser signed in as somebody is who that request is from, wall or no wall.</summary>
    [Fact]
    public async Task AGrantsPerson_OutranksBoth()
    {
        var person = new EfPerson
        {
            Id = Guid.NewGuid(),
            Name = "Ada",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        var grant = NewGrant(person.Id);
        grant.Person = person;

        var context = NewContext();
        context.Request.Headers[LocalCaller.RunnerHeader] = "host:/src";
        context.SetAuthGrant(grant);

        var identity = NewIdentity(context, new StubAuthService());

        Assert.Null(await identity.LocalAsync(default));
        Assert.Equal("Ada", await identity.ActorNameAsync(default));
        Assert.False(await identity.IsProgramAsync(default));
    }

    /// <summary>
    /// The braces to AuthMiddleware's belt. The middleware strips the header
    /// wherever the wall is up; this refuses to read it even when something
    /// else put it there, so neither alone is load-bearing.
    /// </summary>
    [Fact]
    public async Task WithTheWallOn_TheLaneIsClosedAndTheHeaderIsIgnored()
    {
        var context = NewContext();
        context.Request.Headers[LocalCaller.RunnerHeader] = "host:/src";

        var settings = new CountingSiteSettings();
        var identity = NewIdentity(
            context,
            new StubAuthService(),
            new AuthOptions { Enabled = true, CookieName = "aerie_grant", LocalPerson = { Name = "Ada" } },
            settings);

        Assert.Null(await identity.LocalAsync(default));
        Assert.Equal(CallerIdentity.Unattributed, await identity.ActorNameAsync(default));
        Assert.False(await identity.IsProgramAsync(default));

        // Not a micro-optimization: a settings read on the hot path of every
        // request under a wall would be a query answering a question that mode
        // does not ask.
        Assert.Equal(0, settings.Reads);
    }

    /// <summary>A runner costs no query - the settings read is on the person branch alone.</summary>
    [Fact]
    public async Task ARunner_CostsNoSettingsRead()
    {
        var context = NewContext();
        context.Request.Headers[LocalCaller.RunnerHeader] = "host:/src";

        var settings = new CountingSiteSettings();
        await NewIdentity(context, new StubAuthService(), null, settings).LocalAsync(default);

        Assert.Equal(0, settings.Reads);
    }

    [Fact]
    public async Task TheLocalCaller_IsResolvedOncePerRequest()
    {
        var settings = new CountingSiteSettings();
        var identity = NewIdentity(NewContext(), new StubAuthService(), null, settings);

        await identity.LocalAsync(default);
        await identity.ActorNameAsync(default);
        await identity.IsProgramAsync(default);

        Assert.Equal(1, settings.Reads);
    }

    /// <summary>
    /// The local person is an actor, not a row in People. Answering here would
    /// give Quill notes owned by somebody who does not exist and AdminGate a
    /// role to read off a row that is not there.
    /// </summary>
    [Fact]
    public async Task TheLocalPerson_IsNotAPersonRow()
    {
        var identity = NewIdentity(NewContext(), new StubAuthService());

        Assert.NotNull(await identity.LocalAsync(default));
        Assert.Null(await identity.PersonAsync(default));
        Assert.Null(await identity.PersonIdAsync(default));
    }

    /// <summary>Background work has no request to be anybody in - and no header to read one off.</summary>
    [Fact]
    public async Task OutsideARequest_ThereIsNoLocalCallerEither()
    {
        var identity = new CallerIdentity(
            new HttpContextAccessor(), new StubAuthService(), NewOptions(), new StubSiteSettings());

        Assert.Null(await identity.LocalAsync(default));
        Assert.Equal(CallerIdentity.Unattributed, await identity.ActorNameAsync(default));
    }

    /// <summary>Counts what the person branch costs, which is the one thing the wall-on path must not pay.</summary>
    private sealed class CountingSiteSettings : ISiteSettingsService
    {
        public int Reads { get; private set; }

        public Task<SiteSettingsSnapshot> GetAsync(CancellationToken ct)
        {
            Reads++;
            return new StubSiteSettings().GetAsync(ct);
        }

        public void Invalidate()
        {
        }
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

    private static CallerIdentity NewIdentity(
        HttpContext context, IAuthService auth, AuthOptions? options = null, ISiteSettingsService? settings = null) =>
        new(
            new HttpContextAccessor { HttpContext = context },
            auth,
            Options.Create(options ?? NewOptions().Value),
            settings ?? new StubSiteSettings());

    private static IOptions<AuthOptions> NewOptions() =>
        Options.Create(new AuthOptions { Enabled = false, CookieName = "aerie_grant" });
}

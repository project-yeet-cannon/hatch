using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Auth;

/// <summary>
/// The grant lifecycle: an invite becomes exactly one grant, a grant works
/// until it is deleted, and neither the code nor the token is ever recoverable
/// from the database. The single-use and expiry cases are the ones a 40-bit
/// code depends on to be safe at all.
/// </summary>
public class AuthServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InviteIsStoredOnlyAsAHash()
    {
        var (service, db, _) = NewService();

        var invite = await service.CreateInviteAsync("Ada's iPhone", isBootstrap: false, CancellationToken.None);

        Assert.Equal(AuthTokens.InviteCodeLength, invite.Code.Length);
        Assert.Equal($"AERIE-{invite.Code[..4]}-{invite.Code[4..]}", invite.FormattedCode);
        Assert.Equal(Now.AddMinutes(15), invite.ExpiresAt);

        var row = await db.AuthInvites.AsNoTracking().SingleAsync();
        Assert.Equal(AuthTokens.Hash(invite.Code), row.CodeHash);
        Assert.Equal("Ada's iPhone", row.Label);
        Assert.Null(row.RedeemedAt);
    }

    [Fact]
    public async Task BootstrapInviteGetsTheLongerTtl()
    {
        var (service, _, _) = NewService();

        var invite = await service.CreateInviteAsync(null, isBootstrap: true, CancellationToken.None);

        // Nobody is standing at the tablet when a deploy finishes - it has to
        // survive the walk from the terminal to the room.
        Assert.Equal(Now.AddHours(1), invite.ExpiresAt);
    }

    [Fact]
    public async Task CreatingAnInviteSweepsExpiredOnes()
    {
        var (service, db, time) = NewService();
        var stale = await service.CreateInviteAsync(null, isBootstrap: false, CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(16));
        var fresh = await service.CreateInviteAsync(null, isBootstrap: false, CancellationToken.None);

        // The EfOAuthState pattern: expired rows are unusable by definition and
        // a household never makes enough of them to be worth a job.
        var remaining = await db.AuthInvites.AsNoTracking().SingleAsync();
        Assert.Equal(fresh.Id, remaining.Id);
        Assert.NotEqual(stale.Id, remaining.Id);
    }

    [Fact]
    public async Task RedeemingMintsAGrantAndReturnsItsTokenExactlyOnce()
    {
        var (service, db, _) = NewService();
        var invite = await service.CreateInviteAsync(null, isBootstrap: false, CancellationToken.None);

        var result = await service.RedeemAsync(
            AuthTokens.FormatInviteCode(invite.Code), "Kitchen tablet", "GeckoView/1.0", "10.0.0.7", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Token);
        Assert.NotNull(result.Grant);
        Assert.Equal("Kitchen tablet", result.Grant.Label);
        Assert.Equal("GeckoView/1.0", result.Grant.UserAgent);
        Assert.Equal("10.0.0.7", result.Grant.LastSeenIp);
        Assert.Null(result.Grant.ExpiresAt);

        var grant = await db.AuthGrants.AsNoTracking().SingleAsync();
        Assert.Equal(AuthTokens.Hash(result.Token), grant.TokenHash);

        var spent = await db.AuthInvites.AsNoTracking().SingleAsync();
        Assert.Equal(Now, spent.RedeemedAt);
        Assert.Equal(grant.Id, spent.RedeemedGrantId);
    }

    [Fact]
    public async Task RedemptionFallsBackToTheLabelTheAdminPreFilled()
    {
        var (service, _, _) = NewService();
        var invite = await service.CreateInviteAsync("Ada's iPhone", isBootstrap: false, CancellationToken.None);

        var result = await service.RedeemAsync(invite.Code, label: "  ", userAgent: null, clientIp: null, CancellationToken.None);

        Assert.Equal("Ada's iPhone", result.Grant?.Label);
    }

    [Fact]
    public async Task AnInviteIsSingleUse()
    {
        var (service, db, _) = NewService();
        var invite = await service.CreateInviteAsync(null, isBootstrap: false, CancellationToken.None);
        Assert.True((await service.RedeemAsync(invite.Code, null, null, null, CancellationToken.None)).Succeeded);

        var second = await service.RedeemAsync(invite.Code, null, null, null, CancellationToken.None);

        Assert.Equal(AuthRedemption.AlreadyRedeemed, second.Error);
        Assert.Null(second.Token);
        Assert.Equal(1, await db.AuthGrants.CountAsync());
    }

    [Fact]
    public async Task AnExpiredInviteIsRefusedWithoutMintingAnything()
    {
        var (service, db, time) = NewService();
        var invite = await service.CreateInviteAsync(null, isBootstrap: false, CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(15));
        var result = await service.RedeemAsync(invite.Code, null, null, null, CancellationToken.None);

        Assert.Equal(AuthRedemption.Expired, result.Error);
        Assert.False(await db.AuthGrants.AnyAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("NOTACODE!")]
    [InlineData("K3M9P2QT")]
    public async Task ACodeThatMatchesNothingIsInvalidRatherThanAnError(string code)
    {
        var (service, _, _) = NewService();

        var result = await service.RedeemAsync(code, null, null, null, CancellationToken.None);

        Assert.Equal(AuthRedemption.InvalidCode, result.Error);
    }

    [Fact]
    public async Task VerifyFindsTheGrantThatIssuedTheToken()
    {
        var (service, _, _) = NewService();
        var token = await Enroll(service);

        var verified = await service.VerifyAsync([token], "10.0.0.7", CancellationToken.None);

        Assert.NotNull(verified);
        Assert.Equal("10.0.0.7", verified.Grant.LastSeenIp);
        // The token that matched comes back with the grant, because the sliding
        // re-issue has to write back that exact secret and a request may have
        // presented more than one.
        Assert.Equal(token, verified.Token);
    }

    [Fact]
    public async Task ARevokedGrantStopsVerifying()
    {
        var (service, _, _) = NewService();
        var token = await Enroll(service);
        var verified = await service.VerifyAsync([token], null, CancellationToken.None);
        var grant = verified!.Grant;

        Assert.True(await service.RevokeGrantAsync(grant.Id, CancellationToken.None));

        // Revocation is a DELETE, which is the entire reason the credential is
        // an opaque row rather than a signed token.
        Assert.Null(await service.VerifyAsync([token], null, CancellationToken.None));
        Assert.False(await service.RevokeGrantAsync(grant.Id, CancellationToken.None));
    }

    [Fact]
    public async Task AnExpiredGrantStopsVerifying()
    {
        var (service, db, time) = NewService();
        var token = await Enroll(service);
        var grant = await db.AuthGrants.SingleAsync();
        grant.ExpiresAt = Now.AddDays(1);
        await db.SaveChangesAsync();

        Assert.NotNull(await service.VerifyAsync([token], null, CancellationToken.None));
        time.Advance(TimeSpan.FromDays(1));
        Assert.Null(await service.VerifyAsync([token], null, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    public async Task VerifyRefusesWhatIsNotAToken(string? token)
    {
        var (service, _, _) = NewService();
        await Enroll(service);

        Assert.Null(await service.VerifyAsync([token], null, CancellationToken.None));
    }

    [Fact]
    public async Task LastSeenIsThrottledBecauseVerifyRunsOnEveryRequest()
    {
        var (service, db, time) = NewService();
        var token = await Enroll(service, clientIp: "10.0.0.7");

        time.Advance(TimeSpan.FromSeconds(30));
        await service.VerifyAsync([token], "10.0.0.7", CancellationToken.None);
        Assert.Equal(Now, (await Grant(db)).LastSeenAt);

        // An unthrottled write here is a write on every request through the
        // wall, which is every request in the app.
        time.Advance(TimeSpan.FromSeconds(31));
        await service.VerifyAsync([token], "10.0.0.7", CancellationToken.None);
        Assert.Equal(Now.AddSeconds(61), (await Grant(db)).LastSeenAt);
    }

    [Fact]
    public async Task AGrantThatMovedNetworksReportsItsNewAddressImmediately()
    {
        var (service, db, time) = NewService();
        var token = await Enroll(service, clientIp: "10.0.0.7");

        time.Advance(TimeSpan.FromSeconds(5));
        await service.VerifyAsync([token], "10.0.0.9", CancellationToken.None);

        // A stale "last seen" minute is free; a stale address is the one column
        // someone actually reads the Sessions page for.
        Assert.Equal("10.0.0.9", (await Grant(db)).LastSeenIp);
    }

    [Fact]
    public async Task AnEmptyInstallHasNoWayIn_AndOnlyALiveInviteCountsAsOne()
    {
        var (service, _, time) = NewService();
        Assert.False(await service.HasAnyAccessAsync(CancellationToken.None));

        await service.CreateInviteAsync(null, isBootstrap: true, CancellationToken.None);
        Assert.True(await service.HasAnyAccessAsync(CancellationToken.None));

        // An invite nobody redeemed in time is not a way in - otherwise a
        // later deploy would decline to mint the bootstrap code that an
        // install with no grants at all still needs.
        time.Advance(TimeSpan.FromHours(2));
        Assert.False(await service.HasAnyAccessAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OneGrantIsAWayInEvenWithNoInvitesLeft()
    {
        var (service, _, _) = NewService();

        await Enroll(service);

        Assert.True(await service.HasAnyAccessAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GrantsAreListedNewestFirst()
    {
        var (service, _, time) = NewService();
        await Enroll(service, label: "Kitchen tablet");
        time.Advance(TimeSpan.FromMinutes(1));
        await Enroll(service, label: "Ada's iPhone");

        var grants = await service.ListGrantsAsync(CancellationToken.None);

        Assert.Equal(["Ada's iPhone", "Kitchen tablet"], grants.Select(g => g.Label));
    }

    private static async Task<string> Enroll(IAuthService service, string? label = "Kitchen tablet", string? clientIp = null)
    {
        var invite = await service.CreateInviteAsync(null, isBootstrap: false, CancellationToken.None);
        var result = await service.RedeemAsync(invite.Code, label, null, clientIp, CancellationToken.None);
        return result.Token!;
    }

    private static async Task<EfAuthGrant> Grant(AerieContext db) =>
        await db.AuthGrants.AsNoTracking().FirstAsync();

    private static (IAuthService Service, AerieContext Db, FakeTimeProvider Time) NewService()
    {
        var options = new DbContextOptionsBuilder<AerieContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new AerieContext(options);
        var time = new FakeTimeProvider(Now);
        var service = new AuthService(
            db,
            Options.Create(new AuthOptions()),
            time,
            NullLogger<AuthService>.Instance);

        return (service, db, time);
    }
}

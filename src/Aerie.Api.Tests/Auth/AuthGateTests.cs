using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Aerie.Api.Services.Media;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Tests.Auth;

/// <summary>
/// The allow-list, which is the part of the wall that fails silently when it
/// is wrong. Two entries take the whole install down in ways that look nothing
/// like an auth bug - the health probes (readiness fails on every pod and the
/// Deployment never becomes available) and the media prefix (Sonos speakers
/// fetch the stream themselves and hold no cookie, so all music stops with no
/// error that mentions authentication) - so the near-miss cases matter as much
/// as the matches: a StartsWith on the raw string would open /mediafoo too.
/// </summary>
public class AuthGateTests
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/media")]
    [InlineData("/media/Beatles/Revolver/01.flac")]
    [InlineData("/api/ui-logs")]
    [InlineData("/api/vm-console-logs")]
    [InlineData("/api/kiosk/provisioning-info")]
    [InlineData("/apps/auth/")]
    [InlineData("/apps/auth/r/K3M9P2QT")]
    [InlineData("/apps/auth/assets/index-BGJobmXl.js")]
    [InlineData("/api/auth/verify")]
    [InlineData("/api/auth/redeem")]
    // Casing is the proxy's to normalize, not ours to depend on.
    [InlineData("/Health/Ready")]
    [InlineData("/Media/track.flac")]
    public async Task ExemptPathsAreServedWithoutACredential(string path)
    {
        var gate = NewGate();

        Assert.True(gate.IsExempt(path, null));
        var decision = await gate.EvaluateAsync(path, "home.example.com", token: null, clientIp: null, CancellationToken.None);
        Assert.Equal(AuthOutcome.Allow, decision.Outcome);
    }

    [Theory]
    // The near misses. Each of these is a path a naive StartsWith would open.
    [InlineData("/mediafoo/track.flac")]
    [InlineData("/media-library/track.flac")]
    [InlineData("/healthz")]
    [InlineData("/health/livez")]
    [InlineData("/api/ui-logs-admin")]
    [InlineData("/api/kiosk/provisioning-info-secret")]
    [InlineData("/api/kiosk/logs")]
    [InlineData("/api/auth/verifyer")]
    [InlineData("/api/auth/grants")]
    [InlineData("/apps/authoring/")]
    // And the ordinary gated surface.
    [InlineData("/api/zones")]
    [InlineData("/apps/admin/devices")]
    [InlineData("/apps/family/storage/c/ABC123")]
    [InlineData("/")]
    public async Task EverythingElseIsChallenged(string path)
    {
        var gate = NewGate();

        Assert.False(gate.IsExempt(path, null));
        var decision = await gate.EvaluateAsync(path, "home.example.com", token: null, clientIp: "10.0.0.7", CancellationToken.None);
        Assert.Equal(AuthOutcome.Challenge, decision.Outcome);
        Assert.Equal(AuthDecision.NoCredential, decision.Reason);
    }

    [Fact]
    public async Task TheMediaPrefixComesFromOptionsRatherThanTheLiteralSlashMedia()
    {
        // A deploy that repoints the library and an allow-list that hardcodes
        // /media is exactly the silent failure this list exists to prevent.
        var gate = NewGate(mediaRequestPath: "/music/");

        Assert.True(gate.IsExempt("/music/Revolver/01.flac", null));
        Assert.False(gate.IsExempt("/media/Revolver/01.flac", null));
        Assert.Equal(AuthOutcome.Allow, (await gate.EvaluateAsync("/music", null, null, null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public void AnUnconfiguredMediaLibraryExemptsNothing()
    {
        // Empty RequestPath must not degrade into "/" and open the whole app.
        var gate = NewGate(mediaRequestPath: "");

        Assert.False(gate.IsExempt("/media/track.flac", null));
        Assert.False(gate.IsExempt("/api/zones", null));
    }

    [Fact]
    public void AnExemptHostBypassesTheWallWhateverThePath()
    {
        // files.<domain> serves the kiosk APK and its checksum, which are
        // public by design - that Ingress simply never gets the annotation,
        // and the in-process gate has to agree with it.
        var gate = NewGate(exemptHosts: ["files.example.com"]);

        Assert.True(gate.IsExempt("/aerie-kiosk.apk", "files.example.com"));
        // A Host header carries a port; the exempt list doesn't.
        Assert.True(gate.IsExempt("/aerie-kiosk.apk", "FILES.example.com:8080"));
        Assert.False(gate.IsExempt("/aerie-kiosk.apk", "home.example.com"));
    }

    [Fact]
    public async Task ALiveGrantIsAuthenticated()
    {
        var auth = new StubAuthService(Grant("Kitchen tablet"));
        var gate = NewGate(auth: auth);

        var decision = await gate.EvaluateAsync("/apps/admin/devices", "home.example.com", "a-token", "10.0.0.7", CancellationToken.None);

        Assert.Equal(AuthOutcome.Authenticated, decision.Outcome);
        Assert.Equal("Kitchen tablet", decision.Grant?.Label);
        Assert.True(decision.IsAllowed);
        Assert.Equal([("a-token", "10.0.0.7")], auth.Verified);
    }

    [Fact]
    public async Task ATokenNoGrantAnswersToIsChallengedAsUnknownRatherThanMissing()
    {
        var gate = NewGate(auth: new StubAuthService(null));

        var decision = await gate.EvaluateAsync("/api/zones", "home.example.com", "a-revoked-token", "10.0.0.7", CancellationToken.None);

        Assert.Equal(AuthOutcome.Challenge, decision.Outcome);
        Assert.Equal(AuthDecision.UnknownGrant, decision.Reason);
    }

    [Fact]
    public async Task WithTheGateOffNothingIsRefusedAndNoCredentialIsEvenLookedAt()
    {
        // One value is the whole rollback, and it is also the whole of local
        // dev - so it is decided in the gate, not in each caller.
        var auth = new StubAuthService(Grant("Kitchen tablet"));
        var gate = NewGate(enabled: false, auth: auth);

        var decision = await gate.EvaluateAsync("/api/zones", "home.example.com", null, null, CancellationToken.None);

        Assert.False(gate.Enabled);
        Assert.Equal(AuthOutcome.Allow, decision.Outcome);
        Assert.Empty(auth.Verified);
    }

    private static EfAuthGrant Grant(string label) => new()
    {
        TokenHash = AuthTokens.Hash("a-token"),
        Label = label,
        Kind = AuthGrantKind.Device,
        CreatedAt = DateTimeOffset.UnixEpoch,
        CookieIssuedAt = DateTimeOffset.UnixEpoch,
    };

    private static AuthGate NewGate(
        bool enabled = true,
        string mediaRequestPath = "/media",
        string[]? exemptHosts = null,
        IAuthService? auth = null) =>
        new(auth ?? new StubAuthService(null),
            Options.Create(new AuthOptions { Enabled = enabled, ExemptHosts = exemptHosts ?? [] }),
            Options.Create(new MediaLibraryOptions { RequestPath = mediaRequestPath }),
            NullLogger<AuthGate>.Instance);
}

/// <summary>Answers with one grant, or none, and records what it was asked - so a test can prove the gate skipped the lookup entirely.</summary>
internal sealed class StubAuthService(EfAuthGrant? grant) : IAuthService
{
    public List<(string? Token, string? ClientIp)> Verified { get; } = [];

    public Task<EfAuthGrant?> VerifyAsync(string? token, string? clientIp, CancellationToken ct)
    {
        Verified.Add((token, clientIp));
        return Task.FromResult(grant);
    }

    public Task<AuthInviteCreated> CreateInviteAsync(string? label, bool isBootstrap, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<AuthRedemption> RedeemAsync(string? code, string? label, string? userAgent, string? clientIp, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<EfAuthGrant>> ListGrantsAsync(CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<bool> RevokeGrantAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> HasAnyAccessAsync(CancellationToken ct) => throw new NotSupportedException();
}

using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;

namespace Aerie.Api.Tests.Auth;

/// <summary>
/// Answers with one grant, or none, and records what it was asked - so a test
/// can prove the gate skipped the credential lookup entirely, or that the
/// middleware wrote a cookie without also proving how AuthService stores one.
/// </summary>
internal sealed class StubAuthService(EfAuthGrant? grant = null) : IAuthService
{
    public List<(string? Token, string? ClientIp)> Verified { get; } = [];

    public List<Guid> CookieIssued { get; } = [];

    public List<Guid> Revoked { get; } = [];

    public List<(string? Code, string? Label, string? UserAgent, string? ClientIp)> Redemptions { get; } = [];

    public List<(string? Label, bool IsBootstrap)> InvitesCreated { get; } = [];

    /// <summary>What ListGrantsAsync answers - every enrolled device, as the admin Sessions page would see it.</summary>
    public List<EfAuthGrant> Grants { get; } = [];

    /// <summary>Whether RevokeGrantAsync found a row. False is how a test reaches the 404.</summary>
    public bool RevokeResult { get; set; } = true;

    /// <summary>What CreateInviteAsync answers, so a test can assert on a code it chose rather than a random one.</summary>
    public AuthInviteCreated InviteResult { get; set; } =
        new(Guid.NewGuid(), "K3M9P2QT", new DateTimeOffset(2026, 8, 21, 12, 15, 0, TimeSpan.Zero), null);

    /// <summary>What RedeemAsync answers. A refusal by default, so a test has to opt into success rather than inherit it.</summary>
    public AuthRedemption RedeemResult { get; set; } = AuthRedemption.Failed(AuthRedemption.InvalidCode);

    public Task<EfAuthGrant?> VerifyAsync(string? token, string? clientIp, CancellationToken ct)
    {
        Verified.Add((token, clientIp));
        return Task.FromResult(grant);
    }

    public Task MarkCookieIssuedAsync(EfAuthGrant issued, CancellationToken ct)
    {
        CookieIssued.Add(issued.Id);
        return Task.CompletedTask;
    }

    public Task<AuthRedemption> RedeemAsync(string? code, string? label, string? userAgent, string? clientIp, CancellationToken ct)
    {
        Redemptions.Add((code, label, userAgent, clientIp));
        return Task.FromResult(RedeemResult);
    }

    public Task<bool> RevokeGrantAsync(Guid id, CancellationToken ct)
    {
        Revoked.Add(id);
        return Task.FromResult(RevokeResult);
    }

    public Task<AuthInviteCreated> CreateInviteAsync(string? label, bool isBootstrap, CancellationToken ct)
    {
        InvitesCreated.Add((label, isBootstrap));
        return Task.FromResult(InviteResult with { Label = label });
    }

    public Task<IReadOnlyList<EfAuthGrant>> ListGrantsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EfAuthGrant>>(Grants);

    public Task<bool> HasAnyAccessAsync(CancellationToken ct) => throw new NotSupportedException();
}

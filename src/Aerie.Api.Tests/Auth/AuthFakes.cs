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
    /// <summary>Every credential set the gate presented, so a test can prove it passed on *all* of them rather than picking one.</summary>
    public List<(IReadOnlyList<string> Tokens, string? ClientIp)> Verified { get; } = [];

    public List<Guid> CookieIssued { get; } = [];

    public List<Guid> Revoked { get; } = [];

    public List<(string? Code, string? Label, string? UserAgent, string? ClientIp)> Redemptions { get; } = [];

    public List<(string? Label, Guid? PersonId, bool IsBootstrap)> InvitesCreated { get; } = [];

    /// <summary>Every link the controller asked for, so a test can prove the endpoint passed on a null (unclaim) rather than skipping the call.</summary>
    public List<(Guid GrantId, Guid? PersonId)> PersonLinks { get; } = [];

    /// <summary>What SetGrantPersonAsync answers. Both refusals are reachable by setting this, which is the only way to test the two different 4xx.</summary>
    public GrantLinkResult LinkResult { get; set; } = GrantLinkResult.Linked;

    /// <summary>What ListGrantsAsync answers - every enrolled device, as the admin Sessions page would see it.</summary>
    public List<EfAuthGrant> Grants { get; } = [];

    /// <summary>Whether RevokeGrantAsync found a row. False is how a test reaches the 404.</summary>
    public bool RevokeResult { get; set; } = true;

    /// <summary>What CreateInviteAsync answers, so a test can assert on a code it chose rather than a random one.</summary>
    public AuthInviteCreated InviteResult { get; set; } =
        new(Guid.NewGuid(), "K3M9P2QT", new DateTimeOffset(2026, 8, 21, 12, 15, 0, TimeSpan.Zero), null);

    /// <summary>What RedeemAsync answers. A refusal by default, so a test has to opt into success rather than inherit it.</summary>
    public AuthRedemption RedeemResult { get; set; } = AuthRedemption.Failed(AuthRedemption.InvalidCode);

    /// <summary>
    /// Answers for whichever presented token the test named in
    /// <see cref="VerifiesToken"/>, or for any of them when it named none -
    /// which is what lets a test put a stale cookie in front of a good one and
    /// assert the good one still wins.
    /// </summary>
    public string? VerifiesToken { get; set; }

    public Task<AuthVerification?> VerifyAsync(IReadOnlyList<string> tokens, string? clientIp, CancellationToken ct)
    {
        Verified.Add((tokens, clientIp));

        if (grant is null) return Task.FromResult<AuthVerification?>(null);

        var matched = VerifiesToken is null
            ? tokens.FirstOrDefault()
            : tokens.FirstOrDefault(t => t == VerifiesToken);

        return Task.FromResult(matched is null ? null : new AuthVerification(grant, matched));
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

    public Task<AuthInviteCreated> CreateInviteAsync(string? label, Guid? personId, bool isBootstrap, CancellationToken ct)
    {
        InvitesCreated.Add((label, personId, isBootstrap));
        return Task.FromResult(InviteResult with { Label = label });
    }

    public Task<GrantLinkResult> SetGrantPersonAsync(Guid grantId, Guid? personId, CancellationToken ct)
    {
        PersonLinks.Add((grantId, personId));
        return Task.FromResult(LinkResult);
    }

    public Task<IReadOnlyList<EfAuthGrant>> ListGrantsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EfAuthGrant>>(Grants);

    // ---- API keys ----

    /// <summary>Every bearer secret the gate presented, so a test can prove it passed the header on rather than reading a cookie.</summary>
    public List<string?> KeysVerified { get; } = [];

    /// <summary>What VerifyApiKeyAsync answers. Null by default, so a test has to opt into a live key rather than inherit one.</summary>
    public EfApiKey? Key { get; set; }

    /// <summary>What ListApiKeysAsync answers - the admin API Keys page's list.</summary>
    public List<EfApiKey> Keys { get; } = [];

    /// <summary>What CreateApiKeyAsync answers; null is how a test reaches the duplicate-name 409.</summary>
    public ApiKeyCreated? MintResult { get; set; }

    public List<(string Name, IReadOnlyList<string> Scopes)> KeysCreated { get; } = [];

    public List<Guid> KeysRevoked { get; } = [];

    /// <summary>Whether RevokeApiKeyAsync found a row. False is how a test reaches the 404.</summary>
    public bool RevokeKeyResult { get; set; } = true;

    public Task<EfApiKey?> VerifyApiKeyAsync(string? secret, CancellationToken ct)
    {
        KeysVerified.Add(secret);
        return Task.FromResult(Key);
    }

    public Task<IReadOnlyList<EfApiKey>> ListApiKeysAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EfApiKey>>(Keys);

    public Task<ApiKeyCreated?> CreateApiKeyAsync(string name, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        KeysCreated.Add((name, scopes));
        return Task.FromResult(MintResult);
    }

    public Task<bool> RevokeApiKeyAsync(Guid id, CancellationToken ct)
    {
        KeysRevoked.Add(id);
        return Task.FromResult(RevokeKeyResult);
    }

    public Task<bool> HasAnyAccessAsync(CancellationToken ct) => throw new NotSupportedException();
}

using System.Net;
using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.Calendar;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Calendar;

/// <summary>
/// Covers when a cached access token is spent and what a rejected grant leaves
/// behind, driving the real GoogleOAuthService over a stubbed transport so the
/// token endpoint's contract is exercised alongside the caching.
/// </summary>
public class GoogleTokenProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReusesTheCachedToken_WhileItIsComfortablyUnexpired()
    {
        var (provider, db, handler, accountId) = NewProvider(
            account => { account.AccessToken = CalendarTestData.Stored("cached"); account.AccessTokenExpiresAt = Now.AddMinutes(30); });

        var token = await provider.GetAccessTokenAsync(accountId, CancellationToken.None);

        Assert.Equal("cached", token);
        Assert.Empty(handler.Requests);
        Assert.Null((await Account(db, accountId)).LastSyncError);
    }

    [Fact]
    public async Task RefreshesWhenTheCachedTokenIsInsideTheMargin()
    {
        // 30 seconds of life left is inside the 60-second margin: it might not
        // survive the request it is about to be spent on.
        var (provider, db, handler, accountId) = NewProvider(
            account => { account.AccessToken = CalendarTestData.Stored("cached"); account.AccessTokenExpiresAt = Now.AddSeconds(30); },
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"access_token":"fresh","expires_in":3600}"""));

        var token = await provider.GetAccessTokenAsync(accountId, CancellationToken.None);

        Assert.Equal("fresh", token);
        Assert.Single(handler.Requests);
        Assert.Equal("stored-refresh", handler.LastForm["refresh_token"]);
    }

    [Fact]
    public async Task PersistsTheRefreshedTokenObfuscated()
    {
        var (provider, db, _, accountId) = NewProvider(
            respond: StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"access_token":"fresh","expires_in":3600}"""));

        await provider.GetAccessTokenAsync(accountId, CancellationToken.None);

        var account = await Account(db, accountId);
        Assert.NotEqual("fresh", account.AccessToken);
        Assert.Equal("fresh", SecretProtector.Unprotect(account.AccessToken!));
        Assert.Equal(Now.AddSeconds(3600), account.AccessTokenExpiresAt);
        // Google reissued no refresh token, so the stored grant is untouched.
        Assert.Equal("stored-refresh", SecretProtector.Unprotect(account.RefreshToken));
    }

    [Fact]
    public async Task StoresARotatedRefreshToken_WhenGoogleSendsOne()
    {
        var (provider, db, _, accountId) = NewProvider(
            respond: StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"access_token":"fresh","refresh_token":"rotated","expires_in":3600}"""));

        await provider.GetAccessTokenAsync(accountId, CancellationToken.None);

        Assert.Equal("rotated", SecretProtector.Unprotect((await Account(db, accountId)).RefreshToken));
    }

    [Fact]
    public async Task InvalidGrant_FlagsTheAccountForReauthAndDropsTheCachedToken()
    {
        var (provider, db, _, accountId) = NewProvider(
            account => { account.AccessToken = CalendarTestData.Stored("stale"); account.AccessTokenExpiresAt = Now.AddSeconds(-1); },
            StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        var token = await provider.GetAccessTokenAsync(accountId, CancellationToken.None);

        Assert.Null(token);
        var account = await Account(db, accountId);
        Assert.True(account.NeedsReauth);
        Assert.Contains("invalid_grant", account.LastSyncError);
        Assert.Null(account.AccessToken);
        Assert.Null(account.AccessTokenExpiresAt);
        // The row survives so the admin page can offer "Reconnect".
        Assert.Equal("family@example.com", account.AccountEmail);
    }

    [Fact]
    public async Task NeedsReauthAccount_ReturnsNullWithoutAskingGoogleAgain()
    {
        var (provider, _, handler, accountId) = NewProvider(account => account.NeedsReauth = true);

        Assert.Null(await provider.GetAccessTokenAsync(accountId, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TransientFailure_RecordsTheErrorButLeavesTheGrantAlone()
    {
        var (provider, db, _, accountId) = NewProvider(
            respond: StubHttpMessageHandler.Json(HttpStatusCode.ServiceUnavailable, """{"error":"backend_error"}"""));

        Assert.Null(await provider.GetAccessTokenAsync(accountId, CancellationToken.None));

        var account = await Account(db, accountId);
        Assert.False(account.NeedsReauth);
        Assert.Contains("backend_error", account.LastSyncError);
    }

    [Fact]
    public async Task SuccessfulRefresh_ClearsAPreviousError()
    {
        var (provider, db, _, accountId) = NewProvider(
            account => account.LastSyncError = "Google token refresh failed: backend_error",
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"access_token":"fresh","expires_in":3600}"""));

        await provider.GetAccessTokenAsync(accountId, CancellationToken.None);

        Assert.Null((await Account(db, accountId)).LastSyncError);
    }

    [Fact]
    public async Task UnreadableRefreshToken_FlagsTheAccountRatherThanThrowing()
    {
        var (provider, db, handler, accountId) = NewProvider(account => account.RefreshToken = "not base64!");

        Assert.Null(await provider.GetAccessTokenAsync(accountId, CancellationToken.None));
        Assert.Empty(handler.Requests);
        Assert.True((await Account(db, accountId)).NeedsReauth);
    }

    [Fact]
    public async Task UnknownAccount_ReturnsNull()
    {
        var (provider, _, handler, _) = NewProvider();

        Assert.Null(await provider.GetAccessTokenAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    private static (GoogleTokenProvider Provider, AerieContext Db, StubHttpMessageHandler Handler, Guid AccountId) NewProvider(
        Action<EfCalendarAccount>? seed = null,
        HttpResponseMessage? respond = null)
    {
        var options = new DbContextOptionsBuilder<AerieContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var account = new EfCalendarAccount
        {
            Provider = CalendarProviders.Google,
            AccountEmail = "family@example.com",
            RefreshToken = CalendarTestData.Stored("stored-refresh"),
            ConnectedAt = Now.AddDays(-1),
        };
        seed?.Invoke(account);

        using (var seedDb = new AerieContext(options))
        {
            seedDb.CalendarAccounts.Add(account);
            seedDb.SaveChanges();
        }

        var handler = new StubHttpMessageHandler(respond ?? StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var time = new FakeTimeProvider(Now);
        var db = new AerieContext(options);
        var oauth = new GoogleOAuthService(
            new StubHttpClientFactory(handler), new StubSiteSettings(), time, NullLogger<GoogleOAuthService>.Instance);

        return (new GoogleTokenProvider(db, oauth, time, NullLogger<GoogleTokenProvider>.Instance), db, handler, account.Id);
    }

    /// <summary>Reads the account back untracked, so an assertion sees what was saved rather than what the provider still has in memory.</summary>
    private static async Task<EfCalendarAccount> Account(AerieContext db, Guid id) =>
        await db.CalendarAccounts.AsNoTracking().SingleAsync(a => a.Id == id);
}

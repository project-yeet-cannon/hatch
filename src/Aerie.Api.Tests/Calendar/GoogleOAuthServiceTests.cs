using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aerie.Api.Services.Calendar;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Calendar;

/// <summary>
/// Covers the shape of what goes out to Google - the authorization URL and the
/// token form - and how the answers come back, against a stubbed transport.
/// </summary>
public class GoogleOAuthServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuildAuthorizationUrl_AsksForOfflineAccessAndForcesConsent()
    {
        var (service, _) = NewService();

        var url = service.BuildAuthorizationUrl("client-id", "https://home.example/api/calendar/oauth/callback", "state-value", "challenge-value");

        var query = new Uri(url).Query;
        Assert.StartsWith(GoogleOAuthService.AuthorizationEndpoint, url);
        // Without access_type=offline there is no refresh token at all, and
        // without prompt=consent a reconnect of an already-granted account
        // silently returns without one.
        Assert.Contains("access_type=offline", query);
        Assert.Contains("prompt=consent", query);
        Assert.Contains("response_type=code", query);
        Assert.Contains("state=state-value", query);
    }

    [Fact]
    public void BuildAuthorizationUrl_CarriesTheS256Challenge()
    {
        var (service, _) = NewService();
        var verifier = Pkce.NewVerifier();
        var challenge = Pkce.ChallengeFor(verifier);

        var url = service.BuildAuthorizationUrl("client-id", "https://home.example/cb", "state-value", challenge);

        Assert.Contains("code_challenge_method=S256", new Uri(url).Query);
        Assert.Contains($"code_challenge={challenge}", new Uri(url).Query);
        // Computed here rather than taken from Pkce, so the test would catch
        // that helper changing hash or encoding out from under the flow.
        Assert.Equal(
            Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            challenge);
    }

    [Fact]
    public void BuildAuthorizationUrl_AsksForNoCalendarScopeBeyondReading()
    {
        var (service, _) = NewService();

        var url = service.BuildAuthorizationUrl("client-id", "https://home.example/cb", "state-value", "challenge");

        // Aerie displays calendars and never writes to them, so a scope that
        // would let it is a bug worth failing a build over.
        var scopes = ScopesFrom(url);
        Assert.Contains("https://www.googleapis.com/auth/calendar.readonly", scopes);
        Assert.All(scopes.Where(s => s.Contains("/auth/calendar")), s => Assert.EndsWith(".readonly", s));
    }

    [Fact]
    public async Task ExchangeCodeAsync_PostsTheVerifierAndClientCredentials()
    {
        var (service, handler) = NewService(StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"at","refresh_token":"rt","expires_in":3599,"id_token":"it"}"""));

        var result = await service.ExchangeCodeAsync("the-code", "the-verifier", "https://home.example/cb", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(GoogleOAuthService.TokenEndpoint, handler.Requests[0].Url);
        Assert.Equal("authorization_code", handler.LastForm["grant_type"]);
        Assert.Equal("the-code", handler.LastForm["code"]);
        Assert.Equal("the-verifier", handler.LastForm["code_verifier"]);
        Assert.Equal("https://home.example/cb", handler.LastForm["redirect_uri"]);
        Assert.Equal("client-id.apps.googleusercontent.com", handler.LastForm["client_id"]);
        Assert.Equal("GOCSPX-secret", handler.LastForm["client_secret"]);
    }

    [Fact]
    public async Task ExchangeCodeAsync_ResolvesExpiresInAgainstTheClock()
    {
        var (service, _) = NewService(StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"at","refresh_token":"rt","expires_in":3599}"""));

        var result = await service.ExchangeCodeAsync("code", "verifier", "https://home.example/cb", CancellationToken.None);

        Assert.Equal(Now.AddSeconds(3599), result.Tokens!.ExpiresAt);
        Assert.Equal("rt", result.Tokens.RefreshToken);
        Assert.Null(result.Tokens.IdToken);
    }

    [Fact]
    public async Task ExchangeCodeAsync_FailsWithoutCallingGoogle_WhenTheCredentialsAreUnset()
    {
        var (service, handler) = NewService(
            settings: new StubSiteSettings(googleClientId: null, googleClientSecret: null));

        var result = await service.ExchangeCodeAsync("code", "verifier", "https://home.example/cb", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("not_configured", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RefreshAsync_SendsTheRefreshGrant()
    {
        var (service, handler) = NewService(StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"fresh","expires_in":3600}"""));

        var result = await service.RefreshAsync("stored-refresh-token", CancellationToken.None);

        Assert.Equal("refresh_token", handler.LastForm["grant_type"]);
        Assert.Equal("stored-refresh-token", handler.LastForm["refresh_token"]);
        Assert.Equal("fresh", result.Tokens!.AccessToken);
        // Google normally reissues an access token only; the caller keeps the
        // refresh token it already holds.
        Assert.Null(result.Tokens.RefreshToken);
    }

    [Fact]
    public async Task RefreshAsync_SurfacesInvalidGrant()
    {
        var (service, _) = NewService(StubHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}"""));

        var result = await service.RefreshAsync("dead-token", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.IsInvalidGrant);
    }

    [Fact]
    public async Task RefreshAsync_FallsBackToTheStatus_WhenTheErrorBodyIsNotJson()
    {
        var (service, _) = NewService(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("<html>upstream is having a day</html>"),
        });

        var result = await service.RefreshAsync("token", CancellationToken.None);

        Assert.Equal("http_503", result.Error);
        Assert.False(result.IsInvalidGrant);
    }

    [Fact]
    public async Task RefreshAsync_ReportsUnreachable_WhenTheTransportFails()
    {
        var service = NewService(new ThrowingHandler());

        var result = await service.RefreshAsync("token", CancellationToken.None);

        Assert.Equal("unreachable", result.Error);
    }

    [Fact]
    public void EmailFromIdToken_ReadsTheEmailClaim()
    {
        var idToken = CalendarTestData.IdToken("""{"iss":"accounts.google.com","email":"family@example.com","email_verified":true}""");

        Assert.Equal("family@example.com", GoogleOAuthService.EmailFromIdToken(idToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("a.!!!not-base64!!!.c")]
    public void EmailFromIdToken_ReturnsNull_ForGarbage(string? idToken)
    {
        Assert.Null(GoogleOAuthService.EmailFromIdToken(idToken));
    }

    [Fact]
    public void EmailFromIdToken_ReturnsNull_WhenThePayloadHasNoEmail()
    {
        Assert.Null(GoogleOAuthService.EmailFromIdToken(CalendarTestData.IdToken("""{"sub":"12345"}""")));
        Assert.Null(GoogleOAuthService.EmailFromIdToken(CalendarTestData.IdToken("""{"email":""}""")));
        Assert.Null(GoogleOAuthService.EmailFromIdToken(CalendarTestData.IdToken("""{"email":42}""")));
    }

    private static string[] ScopesFrom(string url) =>
        Uri.UnescapeDataString(new Uri(url).Query)
            .Split('&')
            .Single(p => p.StartsWith("scope=", StringComparison.Ordinal))["scope=".Length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static (GoogleOAuthService Service, StubHttpMessageHandler Handler) NewService(
        HttpResponseMessage? response = null,
        StubSiteSettings? settings = null)
    {
        var handler = new StubHttpMessageHandler(response ?? StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        return (NewService(handler, settings), handler);
    }

    private static GoogleOAuthService NewService(HttpMessageHandler handler, StubSiteSettings? settings = null) =>
        new(new StubHttpClientFactory(handler),
            settings ?? new StubSiteSettings(),
            new FakeTimeProvider(Now),
            NullLogger<GoogleOAuthService>.Instance);

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("no route to host");
    }
}

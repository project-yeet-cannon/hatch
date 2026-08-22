using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.WebUtilities;

namespace Aerie.Api.Services.Calendar;

/// <summary>Tokens as Google's token endpoint returns them, with <c>expires_in</c> already resolved to an instant.</summary>
/// <param name="RefreshToken">Only present when Google chose to issue one - an authorization with <c>access_type=offline</c> and <c>prompt=consent</c>, or the rare rotation on a refresh.</param>
public record GoogleTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? IdToken);

/// <summary>
/// The token endpoint's answer: tokens, or the error code Google named. The
/// code matters because callers branch on it - <c>invalid_grant</c> is a
/// revoked or expired grant that only re-consent fixes, while everything else
/// is worth retrying on the next firing.
/// </summary>
public record GoogleTokenResult(GoogleTokens? Tokens, string? Error)
{
    /// <summary>Google's code for "this grant is dead" - revoked, expired, or (while the consent screen is in Testing) older than seven days.</summary>
    public const string InvalidGrant = "invalid_grant";

    public static GoogleTokenResult Ok(GoogleTokens tokens) => new(tokens, null);

    public static GoogleTokenResult Failed(string error) => new(null, error);

    [MemberNotNullWhen(true, nameof(Tokens))]
    public bool Succeeded => Tokens is not null;

    public bool IsInvalidGrant => Error == InvalidGrant;
}

public interface IGoogleOAuthService
{
    /// <summary>The URL to send the admin's browser to. Everything the callback needs to finish the flow is carried in <paramref name="state"/>, which indexes the EfOAuthState row.</summary>
    string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string codeChallenge);

    /// <summary><paramref name="redirectUri"/> has to be the same string that went out with the authorization request - Google compares them.</summary>
    Task<GoogleTokenResult> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct);

    Task<GoogleTokenResult> RefreshAsync(string refreshToken, CancellationToken ct);

    /// <summary>Best-effort revocation, telling Google to forget a grant Aerie is about to delete. Returns whether Google accepted it; a false is worth logging, not worth blocking on.</summary>
    Task<bool> RevokeAsync(string token, CancellationToken ct);
}

/// <summary>
/// Google's OAuth 2.0 endpoints, called directly rather than through
/// Google.Apis.Auth - three endpoints and a revoke, against an SDK whose main
/// value here would be a credential store Aerie already has in Postgres (see
/// docs/kiosk-architecture.md).
///
/// Fail-soft like WeatherService: every method returns a result, never throws
/// a transport failure at its caller. A dead or throttling Google leaves the
/// calendar stale, which is the intended degradation.
/// </summary>
public class GoogleOAuthService(
    IHttpClientFactory httpClientFactory,
    ISiteSettingsService siteSettings,
    TimeProvider time,
    ILogger<GoogleOAuthService> logger) : IGoogleOAuthService
{
    /// <summary>The named HttpClient registered in Program.cs. No BaseAddress - OAuth and the Calendar API answer on different hosts, so every call here is absolute.</summary>
    public const string HttpClientName = "Google";

    public const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    /// <summary><c>openid</c>+<c>email</c> identify which account was connected; the calendar scope is read-only because Aerie displays calendars and never writes to them.</summary>
    public const string Scope = "openid email https://www.googleapis.com/auth/calendar.readonly";

    /// <summary>What to assume when Google omits <c>expires_in</c>. Its access tokens are an hour today; assuming less only costs an early refresh.</summary>
    private const int DefaultExpiresInSeconds = 3600;

    public string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string codeChallenge) =>
        QueryHelpers.AddQueryString(AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,
            // access_type=offline is what makes Google issue a refresh token at
            // all. prompt=consent is what makes it issue one *again* when an
            // already-authorized account reconnects - without it that second
            // authorization comes back with an access token only, and the
            // reconnect the admin just performed to fix a dead grant would
            // leave the account exactly as dead.
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        });

    public Task<GoogleTokenResult> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
        }, ct);

    public Task<GoogleTokenResult> RefreshAsync(string refreshToken, CancellationToken ct) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct);

    public async Task<bool> RevokeAsync(string token, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
            using var response = await client.PostAsync(RevokeEndpoint, content, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (IsTransport(ex, ct))
        {
            logger.LogWarning(ex, "Revoking a Google token failed");
            return false;
        }
    }

    /// <summary>
    /// Reads <c>email</c> out of the id token's payload without validating the
    /// signature. That is deliberate, not an oversight: this JWT arrived in the
    /// body of Aerie's own TLS request to Google's token endpoint, so there is
    /// no untrusted hop between issuer and reader for a signature to defend
    /// against. Validation would earn its keep if the token came from a client.
    /// </summary>
    public static string? EmailFromIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;

        var segments = idToken.Split('.');
        if (segments.Length != 3) return null;

        try
        {
            using var payload = JsonDocument.Parse(WebEncoders.Base64UrlDecode(segments[1]));
            return payload.RootElement.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String
                ? NullIfEmpty(email.GetString())
                : null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or JsonException)
        {
            return null;
        }
    }

    private async Task<GoogleTokenResult> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        var settings = await siteSettings.GetAsync(ct);
        if (settings.GoogleClientId is not { } clientId || settings.GoogleClientSecret is not { } clientSecret)
            return GoogleTokenResult.Failed("not_configured");

        form["client_id"] = clientId;
        form["client_secret"] = clientSecret;

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var content = new FormUrlEncodedContent(form);
            using var response = await client.PostAsync(TokenEndpoint, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = ErrorCodeFrom(body) ?? $"http_{(int)response.StatusCode}";
                // The code, never the body: an error response echoes back parts
                // of a request that carried the client secret and the grant.
                logger.LogWarning("Google token request rejected with {Status} {Error}", (int)response.StatusCode, error);
                return GoogleTokenResult.Failed(error);
            }

            if (JsonSerializer.Deserialize<TokenResponse>(body) is not { AccessToken: { Length: > 0 } accessToken } payload)
            {
                logger.LogWarning("Google token request succeeded but carried no access token");
                return GoogleTokenResult.Failed("no_access_token");
            }

            return GoogleTokenResult.Ok(new GoogleTokens(
                accessToken,
                NullIfEmpty(payload.RefreshToken),
                time.GetUtcNow().AddSeconds(payload.ExpiresIn ?? DefaultExpiresInSeconds),
                NullIfEmpty(payload.IdToken)));
        }
        catch (Exception ex) when (IsTransport(ex, ct))
        {
            logger.LogWarning(ex, "Google token request could not be completed");
            return GoogleTokenResult.Failed("unreachable");
        }
    }

    /// <summary>Google reports failures as <c>{"error":"invalid_grant", ...}</c>. A body that isn't that shape leaves the caller with the HTTP status instead.</summary>
    private static string? ErrorCodeFrom(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? NullIfEmpty(error.GetString())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The failures that mean "Google didn't answer" rather than "the caller
    /// gave up" - HttpClient surfaces its own timeout as a TaskCanceledException,
    /// so the token has to be checked to tell the two apart.
    /// </summary>
    private static bool IsTransport(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("id_token")] string? IdToken);
}

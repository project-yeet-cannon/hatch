using System.Net;
using System.Text;
using Aerie.Api.Common;
using Aerie.Api.Services.DeviceMapping;

namespace Aerie.Api.Tests.Calendar;

/// <summary>A SiteSettings snapshot with the Google credentials filled in, so a test only names the parts it cares about.</summary>
internal sealed class StubSiteSettings(
    string? googleClientId = "client-id.apps.googleusercontent.com",
    string? googleClientSecret = "GOCSPX-secret",
    string? googleOAuthRedirectUri = null) : ISiteSettingsService
{
    public Task<SiteSettingsSnapshot> GetAsync(CancellationToken ct) => Task.FromResult(new SiteSettingsSnapshot(
        TimeZone: "America/New_York",
        Latitude: 40.7128,
        Longitude: -74.0060,
        WeatherEntity: null,
        ComfortToleranceF: 2m,
        DefaultComfortLowF: 68m,
        DefaultComfortHighF: 72m,
        MediaLibraryBaseUrl: null,
        OverrideBackoffMinutes: 120,
        GoogleClientId: googleClientId,
        GoogleClientSecret: googleClientSecret,
        GoogleOAuthRedirectUri: googleOAuthRedirectUri,
        CalendarAgendaDays: 2));
}

/// <summary>Records every request and answers from a queue of canned responses, so a test can assert on the form Google would have received.</summary>
internal sealed class StubHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private int next;

    public List<(string Url, string Body)> Requests { get; } = [];

    /// <summary>The most recent request's form fields, parsed back out of the encoded body.</summary>
    public IReadOnlyDictionary<string, string> LastForm => ParseForm(Requests[^1].Body);

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.RequestUri!.ToString(), body));
        return responses[Math.Min(next++, responses.Length - 1)];
    }

    private static Dictionary<string, string> ParseForm(string body) => body
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')));
}

internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal static class CalendarTestData
{
    /// <summary>An unsigned JWT with the given payload. Enough for EmailFromIdToken, which reads the payload and deliberately doesn't verify the signature.</summary>
    public static string IdToken(string payloadJson) =>
        $"{Base64Url("""{"alg":"none"}""")}.{Base64Url(payloadJson)}.signature";

    /// <summary>Obfuscates the way SettingsController and the OAuth callback do, so a seeded account looks like one Aerie wrote.</summary>
    public static string Stored(string plaintext) => SecretObfuscator.Obfuscate(plaintext);

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

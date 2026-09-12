using System.Net;
using System.Text;

namespace Hatch.Api.Tests;

/// <summary>One request as the stub handler saw it, including the headers - which for some callers (NWS's mandatory User-Agent) are the thing under test.</summary>
internal sealed record RecordedRequest(string Method, string Url, string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Records every request and answers from a queue of canned responses, so a
/// test can assert on the form the upstream would have received. Lives at the
/// test root rather than beside one feature's fakes because nothing about it
/// is feature-specific - the calendar and hazard providers both talk HTTP.
/// </summary>
internal sealed class StubHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private int next;

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>The Authorization header on each request that carried one, so a test can assert the token was actually presented.</summary>
    public IReadOnlyList<string> AuthorizationHeaders =>
        [.. Requests.Select(r => r.Headers.GetValueOrDefault("Authorization")).OfType<string>()];

    /// <summary>The most recent request's form fields, parsed back out of the encoded body.</summary>
    public IReadOnlyDictionary<string, string> LastForm => ParseForm(Requests[^1].Body);

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!.ToString(), body, HeadersOf(request)));
        return responses[Math.Min(next++, responses.Length - 1)];
    }

    /// <summary>
    /// The headers as they would go on the wire, read back out of the
    /// collection's own rendering rather than by joining each header's values
    /// with a comma - User-Agent is the case that punishes the shortcut, since
    /// it parses into several values that are re-joined with a space.
    /// </summary>
    private static Dictionary<string, string> HeadersOf(HttpRequestMessage request) => request.Headers.ToString()
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split(':', 2))
        .Where(parts => parts.Length == 2)
        .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> ParseForm(string body) => body
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')));
}

internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

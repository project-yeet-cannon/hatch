using System.Net;
using System.Text;
using System.Text.Json;

namespace Hatch.Cli.Tests;

/// <summary>One request the runner made.</summary>
public sealed record Call(string Method, string Path, string Query, string Body)
{
    public string Route => $"{Method} {Path}";

    public override string ToString() => $"{Route}{Query} {Body}";

    /// <summary>The body, read as the record it was sent as.</summary>
    public T Read<T>() => JsonSerializer.Deserialize<T>(Body, Fixtures.Json)!;
}

/// <summary>
/// Hatch, answered from a table.
/// </summary>
/// <remarks>
/// Every request is recorded in order, which is what most of these tests
/// actually assert on: that the claim was posted before the session was spawned,
/// that exactly one release followed it, that a dry run posted nothing at all.
/// A route nobody scripted answers 404 naming itself, so a test that meant to
/// stub something and did not finds out immediately.
/// </remarks>
public sealed class Wire : HttpMessageHandler
{
    private sealed record Rule(string Route, HttpStatusCode Code, string Body)
    {
        public int Left { get; set; } = int.MaxValue;
    }

    private readonly List<Rule> _rules = [];
    private readonly List<Call> _calls = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<Call> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    /// <summary>Every call to this route, in order.</summary>
    public IReadOnlyList<Call> To(string method, string path) =>
        Calls.Where(c => c.Route == $"{method} {path}").ToList();

    public int Count(string method, string path) => To(method, path).Count;

    /// <summary>Answer this route with a body, for good.</summary>
    public Wire Reply(string method, string path, HttpStatusCode code, string body = "")
    {
        lock (_gate) _rules.Add(new Rule($"{method} {path}", code, body));
        return this;
    }

    /// <summary>Answer this route with a record, serialised the way the server would.</summary>
    public Wire Json(string method, string path, object body) =>
        Reply(method, path, HttpStatusCode.OK, JsonSerializer.Serialize(body, Fixtures.Json));

    /// <summary>Answer this route this way once, then fall through to whatever is behind it.</summary>
    public Wire Once(string method, string path, HttpStatusCode code, string body = "")
    {
        lock (_gate) _rules.Add(new Rule($"{method} {path}", code, body) { Left = 1 });
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var call = new Call(
            request.Method.Method,
            uri.AbsolutePath,
            uri.Query,
            request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult() ?? "");

        Rule? matched;
        lock (_gate)
        {
            _calls.Add(call);
            matched = _rules.FirstOrDefault(r => r.Route == call.Route && r.Left > 0);
            if (matched is not null && matched.Left != int.MaxValue) matched.Left--;
        }

        var response = matched is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"no rule for {call.Route}", Encoding.UTF8, "text/plain"),
            }
            : new HttpResponseMessage(matched.Code)
            {
                Content = new StringContent(matched.Body, Encoding.UTF8, "application/json"),
            };

        return Task.FromResult(response);
    }
}

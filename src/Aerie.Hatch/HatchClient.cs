using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Aerie.Hatch;

/// <summary>What came back: the code, and the body, and no opinion about either.</summary>
/// <param name="Status">
/// The code, or null where the request never reached a server at all - which is
/// a different thing from a refusal and is treated as one everywhere below.
/// </param>
public readonly record struct Answer(HttpStatusCode? Status, string Body)
{
    public bool Ok => Status is { } code && (int)code is >= 200 and < 300;

    /// <summary>Whether this is the one refusal a claim treats as an answer rather than a fault.</summary>
    public bool Conflict => Status == HttpStatusCode.Conflict;

    /// <summary>The sentence to print, which for Hatch is usually the body: its errors are worth reading.</summary>
    public string Sentence => Body.Trim().Length > 0
        ? Body.Trim().Trim('"')
        : Status is { } code ? $"{(int)code}" : "the origin did not answer";
}

/// <summary>A call that failed in a way the caller had no answer for.</summary>
public sealed class HatchException(string message) : Exception(message);

/// <summary>
/// A body that is already JSON, sent as it was typed.
/// </summary>
/// <remarks>
/// Only <c>hatch api</c> has one. Every other call builds a record and lets the
/// serialiser write it; the passthrough takes whatever was on the command line,
/// and re-encoding that as a JSON string would send the text of the object
/// rather than the object.
/// </remarks>
public readonly record struct RawJson(string Value);

/// <summary>
/// The one place that talks to Hatch, and the one place that decides what a
/// failure means.
/// </summary>
/// <remarks>
/// Two ways to ask, deliberately. <see cref="GetAsync{T}"/> and friends throw,
/// because for every call the runner makes about a ticket a refusal is a fault
/// and the sentence Hatch wrote is the thing worth printing. <see cref="Send"/>
/// hands back the code instead, because the claim's three calls each have a
/// refusal that is an answer - a ticket somebody else is working is not an
/// error, and a loop that ended because a ticket was busy would be the opposite
/// of what the claim is for.
/// </remarks>
public sealed class HatchClient : IDisposable
{
    /// <summary>
    /// Hatch speaks camelCase, and reads it tolerantly.
    /// </summary>
    /// <remarks>
    /// The resolver is source-generated rather than reflective, because
    /// <c>make publish-hatch</c> trims the binary and a trimmed application has
    /// reflection-based serialization switched off outright. See
    /// <see cref="HatchJson"/>.
    /// </remarks>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = HatchJson.Default,
    };

    /// <summary>
    /// The reader or writer for a type, from the generated table.
    /// </summary>
    /// <remarks>
    /// Every serialising call goes through here rather than through
    /// <c>JsonSerializer.Deserialize&lt;T&gt;(string, JsonSerializerOptions)</c>,
    /// which the trimmer cannot see through and warns about at every call site.
    /// A type nobody registered throws here, naming itself.
    /// </remarks>
    private static JsonTypeInfo TypeInfo(Type type)
    {
        try
        {
            return Json.GetTypeInfo(type);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            throw new HatchException(
                $"hatch: {type.Name} is not registered in HatchJson - add a [JsonSerializable] for it.");
        }
    }

    /// <summary>
    /// The header a keyless call names itself with. The same one
    /// <c>LocalCaller</c> reads on the server, spelled once on each side.
    /// </summary>
    public const string RunnerHeader = "X-Hatch-Runner";

    private readonly HttpClient _http;
    private readonly bool _owned;

    /// <param name="runnerName">
    /// What a keyless call calls itself. Only read when there is no key, and
    /// only read at all by a Hatch with its wall off - see
    /// docs/auth-architecture.md, "Local mode".
    /// </param>
    public HatchClient(Settings settings, string? runnerName = null, HttpMessageHandler? handler = null)
    {
        _owned = handler is null;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: _owned)
        {
            BaseAddress = new Uri(settings.Base + "/"),
            Timeout = TimeSpan.FromMinutes(2),
        };

        // One or the other, never both. A key is a credential and outranks a
        // name; the header is only a name, and only a Hatch with its wall off
        // reads one.
        Keyed = !string.IsNullOrWhiteSpace(settings.Key);
        if (Keyed)
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.Key);
        else
            _http.DefaultRequestHeaders.Add(RunnerHeader, runnerName ?? "hatch");

        Origin = settings.Base;
    }

    /// <summary>The origin, for the sentences that name it.</summary>
    public string Origin { get; }

    /// <summary>
    /// Whether a credential was sent. What makes a 401 two different sentences:
    /// a key that was refused is a key to go and look at, and no key at all is a
    /// Hatch with its wall on and nothing to look at yet.
    /// </summary>
    public bool Keyed { get; }

    /// <summary>
    /// The call itself, with no opinion about what a failure means.
    /// </summary>
    /// <remarks>
    /// Nothing here throws. A connection that never happened comes back as a
    /// null status, which every caller treats as a blip rather than as a
    /// refusal - the difference matters most to the heartbeat, where one is a
    /// lost lease and the other is a minute of bad network.
    /// </remarks>
    public async Task<Answer> Send(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (body is RawJson raw)
            request.Content = new StringContent(raw.Value, Encoding.UTF8, "application/json");
        else if (body is not null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, TypeInfo(body.GetType())), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.SendAsync(request, ct);
            return new Answer(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new Answer(null, e.Message);
        }
    }

    /// <summary>
    /// A read whose refusal is a fault. The sentence is Hatch's own where it
    /// wrote one, because "AER-12 is in another project" is worth more than
    /// "400".
    /// </summary>
    /// <returns>
    /// The parsed body, or null on a <c>204</c> - which is the answer a
    /// finished board gives to <c>work/next</c> and not a failure.
    /// </returns>
    public async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        var answer = await Send(HttpMethod.Get, path, null, ct);
        if (!answer.Ok) throw new HatchException(Refusal(answer, path));
        if (answer.Body.Trim().Length == 0) return null;

        try
        {
            return (T?)JsonSerializer.Deserialize(answer.Body, TypeInfo(typeof(T)));
        }
        catch (JsonException e)
        {
            throw new HatchException($"hatch: {path} answered with something that is not {typeof(T).Name}: {e.Message}");
        }
    }

    /// <summary>A write whose refusal is a fault, answering with whatever came back.</summary>
    public Task<T?> PostAsync<T>(string path, object body, CancellationToken ct) where T : class =>
        WriteAsync<T>(HttpMethod.Post, path, body, ct);

    /// <summary>The same, for the verbs a patch and a delete need.</summary>
    public async Task<T?> WriteAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        where T : class
    {
        var answer = await Send(method, path, body, ct);
        if (!answer.Ok) throw new HatchException(Refusal(answer, path));
        if (answer.Body.Trim().Length == 0) return null;

        return (T?)JsonSerializer.Deserialize(answer.Body, TypeInfo(typeof(T)));
    }

    /// <summary>The four refusals worth their own sentence, and everything else.</summary>
    internal string Refusal(Answer answer, string path) => answer.Status switch
    {
        null => $"hatch: could not reach {Origin} - {answer.Body}",
        // No credential was sent, so nothing was rejected: this Hatch has its wall
        // up and wants one. Said plainly, because "the key was not accepted"
        // would send somebody looking at a key they never set.
        HttpStatusCode.Unauthorized when !Keyed =>
            "hatch: 401 - no key was sent and this Hatch has its wall on. Run `hatch config`.",
        HttpStatusCode.Unauthorized =>
            "hatch: 401 - the key was not accepted. Minted, not revoked, copied whole?",
        HttpStatusCode.Forbidden =>
            "hatch: 403 - the key is good and this route is not one it may take (CLAUDE.md).",
        HttpStatusCode.NotFound => $"hatch: 404 - no such issue or route: {path}",
        var code => $"hatch: {(int)code!} - {answer.Body}",
    };

    public void Dispose() => _http.Dispose();
}

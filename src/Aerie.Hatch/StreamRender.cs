using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aerie.Hatch;

/// <summary>
/// What one run said about itself, out of the events it emitted.
/// </summary>
/// <remarks>
/// The shell carried these back through the pipe wearing a control character,
/// because every stage of a pipeline is a subshell and nothing set in one comes
/// back. Here they are simply an object the renderer writes to.
/// </remarks>
public sealed class RunFacts
{
    /// <summary>What <c>claude --resume</c> takes.</summary>
    public string? SessionId { get; set; }

    /// <summary>What it cost, out of the result event.</summary>
    public decimal? CostUsd { get; set; }

    /// <summary>The whole work log entry, or null on a run that ended before it could report.</summary>
    public WorkLogEntryRequest? Result { get; set; }
}

/// <summary>
/// The CLI's <c>stream-json</c>, as lines somebody can read at a terminal.
/// </summary>
/// <remarks>
/// <para>Every event, rather than the CLI's own live rendering: this runs in
/// print mode, where there is no live rendering to have. What it draws is
/// deliberately thin - a tool call is one line naming the one field of it worth
/// a line of terminal, and a tool result is drawn only when it failed, because
/// a tool that worked is told by the next line happening at all.</para>
///
/// <para>An event that will not parse is skipped rather than fatal. The CLI is
/// entitled to say something on stdout that is not an event - a warning, an
/// update notice - and a log that ended on one would be a log lost for a line
/// nobody needed.</para>
/// </remarks>
public sealed partial class StreamRender(string root, RunFacts facts)
{
    private const int Width = 96;
    private const int ThinkingStep = 3_000;

    private long _thinking;
    private long _said;

    /// <summary>The lines one event turns into, in order. Empty for the events that draw nothing.</summary>
    public IEnumerable<string> Read(string raw)
    {
        var line = raw.TrimStart();
        if (!line.StartsWith('{')) return [];

        JsonElement e;
        try
        {
            e = JsonDocument.Parse(line).RootElement;
        }
        catch (JsonException)
        {
            return [];
        }

        var type = Text(e, "type");
        return type switch
        {
            "system" when Text(e, "subtype") == "init" => Init(e),
            "system" when Text(e, "subtype") == "thinking_tokens" => Thinking(e),
            "assistant" => Assistant(e),
            "user" => Failures(e),
            "result" => Result(e),
            _ => [],
        };
    }

    private IEnumerable<string> Init(JsonElement e)
    {
        var id = Text(e, "session_id");
        if (id is null) yield break;

        facts.SessionId = id;
        yield return $"hatch: session {id}";
        yield return Resume(id);
    }

    /// <summary>
    /// Thinking is the longest silence a run produces and the one most often
    /// mistaken for a hang. Reported every few thousand tokens: often enough to
    /// be a pulse, rarely enough not to become the transcript.
    /// </summary>
    private IEnumerable<string> Thinking(JsonElement e)
    {
        if (e.TryGetProperty("estimated_tokens", out var tokens) && tokens.TryGetInt64(out var estimated))
            _thinking = estimated;

        if (_thinking < _said + ThinkingStep) yield break;

        _said = _thinking;
        yield return $"  ✻ thinking… {_thinking / 1000}k tokens";
    }

    private IEnumerable<string> Assistant(JsonElement e)
    {
        foreach (var part in Content(e))
        {
            switch (Text(part, "type"))
            {
                case "tool_use":
                    yield return $"  ⏺ {Text(part, "name")}  {Summarise(part)}";
                    break;

                case "text" when Flat(Text(part, "text") ?? "") is { Length: > 0 }:
                    yield return "";
                    foreach (var said in (Text(part, "text") ?? "").ReplaceLineEndings("\n").Split('\n'))
                        yield return said;
                    break;
            }
        }
    }

    /// <summary>
    /// Only failures. Echoing every tool result would bury the calls under their
    /// own output, and a call that worked is told by the next one happening.
    /// </summary>
    private IEnumerable<string> Failures(JsonElement e)
    {
        foreach (var part in Content(e))
        {
            if (Text(part, "type") != "tool_result") continue;
            if (!part.TryGetProperty("is_error", out var failed) || failed.ValueKind != JsonValueKind.True) continue;

            var said = part.TryGetProperty("content", out var content)
                ? content.ValueKind switch
                {
                    JsonValueKind.Array => string.Join(' ', content.EnumerateArray().Select(c => Text(c, "text") ?? "")),
                    JsonValueKind.String => content.GetString() ?? "",
                    _ => content.ToString(),
                }
                : "";

            yield return $"  ✗ {Clip(Flat(said))}";
        }
    }

    private IEnumerable<string> Result(JsonElement e)
    {
        var id = Text(e, "session_id") ?? facts.SessionId ?? "";
        var cost = Decimal(e, "total_cost_usd");
        var duration = Long(e, "duration_ms");
        var turns = (int)Long(e, "num_turns");
        var failed = e.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;

        facts.SessionId = id;
        if (cost is { } spent) facts.CostUsd = spent;

        var (title, summary) = WorkLog(Text(e, "result") ?? "");

        facts.Result = new WorkLogEntryRequest(
            SessionId: id,
            StartedAt: default,
            EndedAt: default,
            DurationMs: duration,
            Title: title,
            Summary: summary,
            IsError: failed,
            Turns: turns,
            CostUsd: cost ?? 0m,
            Models: Models(e));

        yield return "";
        yield return "hatch: " + (failed ? "ended with an error" : "done")
            + $" in {Clock(duration / 1000)}, {turns} turns"
            + (cost is { } money ? $", ${money.ToString("0.##", CultureInfo.InvariantCulture)}" : "");
        yield return Resume(id);
    }

    /// <summary>
    /// The per-model breakdown, in Hatch's names rather than the CLI's.
    /// </summary>
    /// <remarks>
    /// Two things about the source are worth knowing and neither is the obvious
    /// reading. First, <c>usage</c> is not the total for the session and
    /// <c>modelUsage</c> is: the latter is the per-model aggregate over the
    /// whole run, and its <c>costUSD</c> sums to <c>total_cost_usd</c> to the
    /// last digit - so the four counts are the sum over <c>modelUsage</c> and
    /// <c>usage</c> is not read at all. Second, <c>usage</c> is snake_case while
    /// the values inside <c>modelUsage</c> are camelCase, in the same object.
    /// </remarks>
    private static List<WorkLogModelUseDto> Models(JsonElement e)
    {
        var models = new List<WorkLogModelUseDto>();
        if (!e.TryGetProperty("modelUsage", out var usage) || usage.ValueKind != JsonValueKind.Object) return models;

        foreach (var model in usage.EnumerateObject())
            models.Add(new WorkLogModelUseDto(
                Model: model.Name,
                InputTokens: Long(model.Value, "inputTokens"),
                OutputTokens: Long(model.Value, "outputTokens"),
                CacheCreationTokens: Long(model.Value, "cacheCreationInputTokens"),
                CacheReadTokens: Long(model.Value, "cacheReadInputTokens"),
                CostUsd: Decimal(model.Value, "costUSD") ?? 0m));

        return models;
    }

    /// <summary>
    /// What the session said it did, out of the fenced block it was asked to
    /// end with - see <see cref="Prompt"/>, which is where that instruction is
    /// written.
    /// </summary>
    /// <remarks>
    /// The first non-empty line is the title (a leading <c>title:</c> comes off,
    /// for the session that writes one), and everything after it is the summary.
    /// The <em>last</em> block wins, so a session that quotes the format earlier
    /// in its own prose does not talk itself out of a title. No block, or one
    /// with nothing in it, leaves both null and the row goes up undescribed -
    /// which is a mark the page draws rather than an entry lost.
    /// </remarks>
    internal static (string? Title, string? Summary) WorkLog(string said)
    {
        var blocks = Fence().Matches(said);
        if (blocks.Count == 0) return (null, null);

        var lines = blocks[^1].Groups[1].Value
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(l => l.TrimEnd())
            .ToList();

        var head = lines.FindIndex(l => l.Trim().Length > 0);
        if (head < 0) return (null, null);

        var title = TitlePrefix().Replace(lines[head].TrimStart(), "");
        if (title.Length > 200) title = title[..200];

        var body = string.Join('\n', lines.Skip(head + 1)).Trim();
        if (body.Length > 2000) body = body[..2000];

        return (title, body.Length == 0 ? null : body);
    }

    [GeneratedRegex(@"```work-log[^\n]*\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex Fence();

    [GeneratedRegex(@"^title:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex TitlePrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // ---- Odds and ends ----

    private static string Resume(string id) => $"hatch:   join it with  claude --resume {id}";

    private static IEnumerable<JsonElement> Content(JsonElement e) =>
        e.TryGetProperty("message", out var message) &&
        message.TryGetProperty("content", out var content) &&
        content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray()
            : [];

    /// <summary>
    /// The one field of a tool call worth a line of terminal. Ordered by how
    /// much it says about what is happening: a command, then a path, then
    /// whatever the call was actually about.
    /// </summary>
    private string Summarise(JsonElement call)
    {
        if (!call.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return "";

        foreach (var field in (string[])["command", "file_path", "pattern", "description", "url", "path", "key"])
            if (Text(input, field) is { Length: > 0 } value)
                return Clip(Flat(Here(value)));

        return Clip(Flat(Here(input.ToString())));
    }

    /// <summary>
    /// Paths as the repository says them. An absolute path to a file in this
    /// tree is most of a terminal line spent on the part that never changes.
    /// </summary>
    private string Here(string said) =>
        root.Length > 0 ? said.Replace(root.TrimEnd('/') + "/", "") : said;

    private static string Flat(string said) => Whitespace().Replace(said, " ").Trim();

    private static string Clip(string said) => said.Length > Width ? said[..(Width - 1)] + "…" : said;

    /// <summary>Minutes and seconds, as somebody reads them off a terminal.</summary>
    public static string Clock(long seconds) => $"{seconds / 60}m{seconds % 60:00}s";

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static decimal? Decimal(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : null;
}

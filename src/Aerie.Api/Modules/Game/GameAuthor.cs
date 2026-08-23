using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Anthropic;
using Anthropic.Models.Messages;
using Aerie.Api.Common;
using Aerie.Api.Services.DeviceMapping;

namespace Aerie.Api.Modules.Game;

/// <summary>Why a turn could not be written, in words a parent can act on.</summary>
public class GameAuthorException(string message, bool isConfiguration = false) : Exception(message)
{
    /// <summary>True when the fix is on the Settings page rather than in the request.</summary>
    public bool IsConfiguration { get; } = isConfiguration;
}

/// <summary>What the model was given to work from.</summary>
/// <param name="CurrentCode">The game as it stands, or null for the first turn.</param>
/// <param name="RecentPrompts">The last few things asked for, oldest first - continuity the code alone doesn't carry.</param>
/// <param name="Instruction">What the player typed, or the error text for a repair.</param>
/// <param name="IsRepair">Whether this turn is fixing a crash rather than adding something.</param>
public record GameAuthorRequest(
    string? CurrentCode, IReadOnlyList<string> RecentPrompts, string Instruction,
    bool IsRepair, GameModelChoice Model);

/// <summary>A written turn, plus what it cost to write.</summary>
public record AuthoredGame(
    string Code, string Summary, string? Extra, string Model,
    int InputTokens, int OutputTokens, int CachedInputTokens, int DurationMs);

public interface IGameAuthor
{
    /// <summary>
    /// Whether this install has an API key at all. Asked before a child is
    /// handed a text box, so an unconfigured install says so on the way in
    /// rather than after the first thing anyone types.
    /// </summary>
    Task<GameCapabilityDto> GetCapabilityAsync(CancellationToken ct);

    Task<AuthoredGame> WriteAsync(GameAuthorRequest request, CancellationToken ct);
}

/// <summary>
/// The turn: engine reference in, a game file out.
/// </summary>
/// <remarks>
/// Three choices worth knowing about.
///
/// <para>The response is delimited rather than JSON. A whole source file inside
/// a JSON string means every newline and quote in the game is escaped, which
/// costs tokens on the most expensive part of the response and adds a class of
/// failure - a single bad escape - that loses the entire turn. Tagged blocks
/// have neither problem, and the tags are the model's native shape for
/// returning code.</para>
///
/// <para>The call streams, and the stream is drained here rather than forwarded
/// to the browser. Streaming is what keeps a 32k-token generation from dying on
/// an HTTP timeout; forwarding it would mean an SSE contract, a partial-code
/// state on the client, and nothing to show for it - a half-written game is not
/// playable, so there is no progress worth rendering token by token.</para>
///
/// <para>The engine reference is marked cacheable with a one-hour TTL. It is
/// most of every request and identical across every turn in the house, so an
/// afternoon of play reads it from cache at a tenth of the price.</para>
/// </remarks>
public class GameAuthor(
    ISiteSettingsService siteSettings, ISecrets secrets, ILogger<GameAuthor> logger) : IGameAuthor
{
    /// <summary>Comfortably above any game the engine can express, and low enough to bound a runaway.</summary>
    private const int MaxOutputTokens = 32000;

    /// <summary>Refused rather than stored: past this, something has gone wrong that a retry fixes and a database row does not.</summary>
    private const int MaxCodeBytes = 200_000;

    /// <summary>
    /// The secrets-file key that stands in for the site setting during local
    /// development, matching how ha_token works (see Secrets.cs).
    /// </summary>
    private const string SecretsKey = "anthropic_api_key";

    // One client per key. The SDK's client owns a connection pool, so building
    // one per turn would open a new one every time a child asks for a puppy.
    private readonly ConcurrentDictionary<string, AnthropicClient> clients = new(StringComparer.Ordinal);

    public async Task<GameCapabilityDto> GetCapabilityAsync(CancellationToken ct)
    {
        var key = await ResolveApiKeyAsync(ct);
        return key is null
            ? new GameCapabilityDto(false, "No Anthropic API key is set. An adult can add one on the admin app's Settings page.")
            : new GameCapabilityDto(true, null);
    }

    public async Task<AuthoredGame> WriteAsync(GameAuthorRequest request, CancellationToken ct)
    {
        var apiKey = await ResolveApiKeyAsync(ct)
            ?? throw new GameAuthorException(
                "No Anthropic API key is set. An adult can add one on the admin app's Settings page.",
                isConfiguration: true);

        var client = clients.GetOrAdd(apiKey, key => new AnthropicClient { ApiKey = key });
        var model = ModelIdFor(request.Model);
        var started = Stopwatch.GetTimestamp();

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxOutputTokens,
            // A list rather than a bare string so the reference can carry a
            // cache breakpoint; everything that varies per turn is in Messages,
            // after it, where it cannot invalidate the prefix.
            System = new List<TextBlockParam>
            {
                new()
                {
                    Text = GameEngineReference.SystemPrompt,
                    CacheControl = new CacheControlEphemeral { Ttl = Ttl.Ttl1h },
                },
            },
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Effort = EffortFor(request.Model) },
            Messages = [new() { Role = Role.User, Content = BuildUserMessage(request) }],
        };

        var text = new StringBuilder();
        var inputTokens = 0;
        var outputTokens = 0;
        var cachedTokens = 0;
        string? stopReason = null;

        try
        {
            await foreach (var streamEvent in client.Messages.CreateStreaming(parameters).WithCancellation(ct))
            {
                if (streamEvent.TryPickContentBlockDelta(out var block) && block.Delta.TryPickText(out var chunk))
                {
                    text.Append(chunk.Text);
                }
                else if (streamEvent.TryPickStart(out var start))
                {
                    inputTokens = (int)start.Message.Usage.InputTokens;
                    cachedTokens = (int)(start.Message.Usage.CacheReadInputTokens ?? 0);
                }
                else if (streamEvent.TryPickDelta(out var messageDelta))
                {
                    outputTokens = (int)messageDelta.Usage.OutputTokens;
                    stopReason = messageDelta.Delta.StopReason?.ToString();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not GameAuthorException)
        {
            logger.LogError(ex, "Game turn failed against {Model}", model);
            throw new GameAuthorException("The game writer could not be reached. Try again in a moment.");
        }

        var duration = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        logger.LogInformation(
            "Game turn on {Model} in {Duration}ms: {Input} in ({Cached} cached), {Output} out, stop {Stop}",
            model, duration, inputTokens, cachedTokens, outputTokens, stopReason ?? "end_turn");

        // "refusal" is the one stop reason that produces a well-formed empty
        // answer, so it needs saying out loud - otherwise it reads to a parent
        // as the app being broken.
        if (string.Equals(stopReason, "refusal", StringComparison.OrdinalIgnoreCase))
            throw new GameAuthorException("The game writer would rather not make that one. Try asking for something else.");

        var parsed = GameAnswerParser.Parse(text.ToString())
            ?? throw new GameAuthorException(
                string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase)
                    ? "That turned into a bigger game than fits in one go. Try asking for one thing at a time."
                    : "The game writer answered with something unusable. Try again.");

        // A truncated answer can still parse - the closing tag is optional -
        // so the stop reason is what decides whether the code is whole.
        if (string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase))
            throw new GameAuthorException("That turned into a bigger game than fits in one go. Try asking for one thing at a time.");

        var code = parsed.Code;
        if (Encoding.UTF8.GetByteCount(code) > MaxCodeBytes)
            throw new GameAuthorException("That turned into a bigger game than fits in one go. Try asking for one thing at a time.");

        // A file that never calls defineGame cannot start, and finding that out
        // here costs a retry; finding it out in the frame costs a black screen
        // and a repair round trip.
        if (!code.Contains("defineGame", StringComparison.Ordinal))
            throw new GameAuthorException("The game writer answered with something unusable. Try again.");

        return new AuthoredGame(
            Code: code,
            Summary: parsed.Summary,
            Extra: parsed.Extra,
            Model: model,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CachedInputTokens: cachedTokens,
            DurationMs: duration);
    }

    /// <summary>
    /// The site setting first, then the local secrets file. Two sources because
    /// they serve different installs: a deployed house sets it once on the
    /// Settings page, and a laptop running `make run` has no admin app open.
    /// </summary>
    private async Task<string?> ResolveApiKeyAsync(CancellationToken ct)
    {
        var settings = await siteSettings.GetAsync(ct);
        var key = settings.AnthropicApiKey ?? secrets.GetSecret(SecretsKey);
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    /// <summary>
    /// The only place a model id is written down. The client sends a speed, not
    /// a model - so a browser cannot name one, and changing what "quick" means
    /// is one line here rather than a deploy of the family app.
    /// </summary>
    private static string ModelIdFor(GameModelChoice choice) =>
        choice == GameModelChoice.Careful ? "claude-opus-5" : "claude-sonnet-5";

    /// <summary>
    /// Effort follows the same lever. Careful is the turn someone chose to wait
    /// for, so it gets the depth; quick is the other ninety per cent, where the
    /// wait is the thing being optimised.
    /// </summary>
    private static Effort EffortFor(GameModelChoice choice) =>
        choice == GameModelChoice.Careful ? Effort.High : Effort.Medium;

    private static string BuildUserMessage(GameAuthorRequest request)
    {
        var message = new StringBuilder();

        if (string.IsNullOrWhiteSpace(request.CurrentCode))
        {
            message.AppendLine("The world is empty. This is the first thing anyone has asked for.");
        }
        else
        {
            message.AppendLine("Here is the game as it runs right now:");
            message.AppendLine();
            message.AppendLine("<current-code>");
            message.AppendLine(request.CurrentCode.Trim());
            message.AppendLine("</current-code>");
        }

        // The code carries what the game is; this carries where it was going.
        // Without it, a turn that says "make it faster" has no idea what "it"
        // was two turns ago.
        if (request.RecentPrompts.Count > 0)
        {
            message.AppendLine();
            message.AppendLine("What they have asked for so far, oldest first:");
            foreach (var prompt in request.RecentPrompts) message.AppendLine($"- {prompt}");
        }

        message.AppendLine();
        if (request.IsRepair)
        {
            message.AppendLine(GameEngineReference.RepairInstruction);
            message.AppendLine();
            message.AppendLine("<error>");
            message.AppendLine(request.Instruction.Trim());
            message.AppendLine("</error>");
        }
        else
        {
            message.AppendLine("Now do this:");
            message.AppendLine();
            message.AppendLine("<request>");
            message.AppendLine(request.Instruction.Trim());
            message.AppendLine("</request>");
        }

        return message.ToString();
    }
}

using System.Text.Json.Serialization;

namespace Hatch.Cli;

/// <summary>
/// Every record that crosses the wire, with its reader and writer generated at
/// compile time instead of discovered by reflection at runtime.
/// </summary>
/// <remarks>
/// <para>Not an optimisation. <c>make publish-hatch</c> trims the binary, and a
/// trimmed .NET application has reflection-based serialization switched off
/// outright - <c>JsonSerializer.Deserialize&lt;BoardDto&gt;</c> throws
/// "Reflection-based serialization has been disabled for this application" on
/// the first call, on the operator's machine and nowhere before it. This is
/// what makes the shipped binary work at all.</para>
///
/// <para>A type that is not listed here throws where it is used rather than
/// coming back with its fields empty, which is the right way round: a wire
/// record nobody registered is a bug in this file, and it says so at the call
/// that needed it.</para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]

// What is read.
[JsonSerializable(typeof(BoardDto))]
[JsonSerializable(typeof(IssueDto))]
[JsonSerializable(typeof(WorkDto))]
[JsonSerializable(typeof(CommentDto))]
[JsonSerializable(typeof(WorkLogEntryDto))]
[JsonSerializable(typeof(ClaimTakenDto))]
[JsonSerializable(typeof(RunnerInstructionDto))]
[JsonSerializable(typeof(ClaudeTokenDto))]
[JsonSerializable(typeof(List<StatusDto>))]
[JsonSerializable(typeof(List<CommentDto>))]
[JsonSerializable(typeof(List<QuestionDto>))]
[JsonSerializable(typeof(List<IssueCardDto>))]
[JsonSerializable(typeof(List<QueueEntryDto>))]

// What is written.
[JsonSerializable(typeof(CommentCreateRequest))]
[JsonSerializable(typeof(IssueMoveRequest))]
[JsonSerializable(typeof(IssuePatchRequest))]
[JsonSerializable(typeof(IssueDependencyRequest))]
[JsonSerializable(typeof(WorkLogEntryRequest))]
[JsonSerializable(typeof(ClaimRequest))]
[JsonSerializable(typeof(ClaimHeartbeatRequest))]
[JsonSerializable(typeof(RunnerHeartbeatRequest))]
[JsonSerializable(typeof(NightState))]
internal sealed partial class HatchJson : JsonSerializerContext;

using System.Text.Json;

namespace Aerie.Api.Models.UiLogs;

/// <summary>One client-side log line shipped by a frontend app's clientLogger (e.g. apps/dashboard/src/lib/clientLogger.ts, apps/admin/src/lib/clientLogger.ts).</summary>
public record UiLogEntry(
    string App,
    string Level,
    string Message,
    DateTimeOffset? Timestamp,
    string? SessionId,
    string? Url,
    JsonElement? Metadata);

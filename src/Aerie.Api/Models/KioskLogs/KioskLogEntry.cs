using System.Text.Json;

namespace Aerie.Api.Models.KioskLogs;

/// <summary>One client-side log line shipped by the kiosk dashboard's clientLogger (apps/dashboard/src/lib/clientLogger.ts).</summary>
public record KioskLogEntry(
    string Level,
    string Message,
    DateTimeOffset? Timestamp,
    string? SessionId,
    string? Url,
    JsonElement? Metadata);

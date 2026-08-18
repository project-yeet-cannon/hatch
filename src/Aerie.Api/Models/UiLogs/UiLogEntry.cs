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
    JsonElement? Metadata,
    // Persisted client-side (localStorage on web, ANDROID_ID for the kiosk
    // shell - see clientLogger.ts/KioskLogger.kt) so the same physical
    // device's lines correlate across page loads/process restarts, unlike
    // SessionId which is per-load. Client-supplied and thus spoofable, same
    // trust level as every other field here - see UiLogsController's remarks.
    string? DeviceId = null);

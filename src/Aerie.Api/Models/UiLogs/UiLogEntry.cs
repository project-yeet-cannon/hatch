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
    string? DeviceId = null,
    // The build that emitted this line - the bundle running in the browser,
    // not the API relaying it (docs/plans/version.md). Stamped into index.html
    // by Aerie.Web/vite-plugin-aerie-revision.mts and read from there by
    // clientLogger.ts.
    //
    // Nullable, and expected to stay null for a long while: a browser holding a
    // bundle from before this shipped is exactly the stale device an
    // administrator wants to find, so the field has to tolerate its own absence
    // rather than reject the line that proves the point.
    string? Revision = null,
    int? Sequence = null);

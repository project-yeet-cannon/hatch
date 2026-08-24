using System.Text.Json;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>One entity crossing the motion/no-motion line, as read off a Home Assistant state_changed event. IsActive is the state it landed in.</summary>
public readonly record struct MotionTransition(string EntityId, bool IsActive);

/// <summary>
/// Pure frame -> transition mapping for the Home Assistant WebSocket API - no
/// socket, no DB, no clock. Kept separate from HomeAssistantEventListener so
/// the part with the actual decisions in it is unit-testable, same convention
/// as ChannelValueExtractor; the socket and reconnect plumbing around it stays
/// untested, matching HomeAssistantStateReader/HomeAssistantCommandService.
/// </summary>
public static class HomeAssistantEventParser
{
    /// <summary>HA's WebSocket message envelope types this app reads. Everything else on the socket is ignored.</summary>
    public const string AuthRequired = "auth_required";
    public const string AuthOk = "auth_ok";
    public const string AuthInvalid = "auth_invalid";
    public const string Result = "result";
    public const string Event = "event";

    /// <summary>The envelope "type" of a frame, or null if the frame isn't an object with a string type (which nothing HA sends is, but the socket is not a trusted parser).</summary>
    public static string? ReadMessageType(string frame)
    {
        if (TryParse(frame) is not { } doc) return null;

        using (doc)
        {
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("type", out var type)
                   && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
        }
    }

    /// <summary>
    /// Whether a raw WebSocket frame represents an entity crossing the
    /// motion/no-motion line, and which way. Null for everything else: frames
    /// that aren't state_changed events, attribute-only updates where the state
    /// string didn't change, and malformed JSON.
    ///
    /// "Motion" is exactly the state "on"; "off", "unavailable", "unknown" and
    /// a missing state all read as no-motion. Folding the unavailable states in
    /// with "off" rather than ignoring them is deliberate - a sensor that drops
    /// off the network mid-detection reports unavailable and never reports off,
    /// and treating that as "still detecting" would leave the kiosk modal open
    /// until the camera came back. We can no longer see motion, so we stop
    /// claiming there is any.
    /// </summary>
    public static MotionTransition? TryReadMotionTransition(string frame)
    {
        if (TryParse(frame) is not { } doc) return null;

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != Event)
                return null;
            if (!root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object)
                return null;
            if (!ev.TryGetProperty("event_type", out var eventType) || eventType.GetString() != "state_changed")
                return null;
            if (!ev.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;
            if (!data.TryGetProperty("entity_id", out var entity) || entity.ValueKind != JsonValueKind.String)
                return null;

            var wasActive = IsMotionActive(ReadState(data, "old_state"));
            var isActive = IsMotionActive(ReadState(data, "new_state"));

            // HA fires state_changed for attribute-only updates too (a motion
            // sensor re-reporting the same "on" with a new last_seen). Those are
            // not transitions and must not reach the dispatcher, which would
            // otherwise re-open a modal the user just dismissed.
            return wasActive == isActive
                ? null
                : new MotionTransition(entity.GetString()!, isActive);
        }
    }

    /// <summary>old_state/new_state are objects, or null when the entity was just added or just removed.</summary>
    private static string? ReadState(JsonElement data, string property) =>
        data.TryGetProperty(property, out var state) && state.ValueKind == JsonValueKind.Object
        && state.TryGetProperty("state", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsMotionActive(string? state) => state == "on";

    /// <summary>Frames arrive off a socket, so a parse failure is an expected input rather than a bug: it costs one ignored frame, not the listener.</summary>
    private static JsonDocument? TryParse(string frame)
    {
        try
        {
            return JsonDocument.Parse(frame);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

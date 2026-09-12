using Hatch.Api.Services.DeviceMapping;

namespace Hatch.Api.Tests.DeviceMapping;

/// <summary>
/// Covers HomeAssistantEventParser - the pure half of HomeAssistantEventListener.
/// The socket, handshake and reconnect plumbing around it stays untested, the
/// same trade HomeAssistantStateReader/HomeAssistantCommandService make with
/// HA-facing code that can't be substituted.
/// </summary>
public class HomeAssistantEventParserTests
{
    /// <summary>A state_changed frame in the shape HA's WebSocket API sends it. Attributes are trimmed to what the parser can see; it reads nothing but entity_id and the two state strings.</summary>
    private static string StateChangedFrame(string entityId, string? oldState, string? newState) =>
        $$"""
        {
          "id": 1,
          "type": "event",
          "event": {
            "event_type": "state_changed",
            "data": {
              "entity_id": "{{entityId}}",
              "old_state": {{StateObject(oldState)}},
              "new_state": {{StateObject(newState)}}
            },
            "origin": "LOCAL",
            "time_fired": "2026-08-23T18:00:00.000000+00:00",
            "context": { "id": "01J000000000000000000000", "parent_id": null, "user_id": null }
          }
        }
        """;

    private static string StateObject(string? state) =>
        state is null
            ? "null"
            : $$"""{ "entity_id": "binary_sensor.front_door_person", "state": "{{state}}", "attributes": { "device_class": "motion" }, "last_changed": "2026-08-23T18:00:00.000000+00:00" }""";

    [Fact]
    public void TryReadMotionTransition_OffToOn_ReportsActive()
    {
        var transition = HomeAssistantEventParser.TryReadMotionTransition(
            StateChangedFrame("binary_sensor.front_door_person", "off", "on"));

        Assert.Equal(new MotionTransition("binary_sensor.front_door_person", true), transition);
    }

    [Fact]
    public void TryReadMotionTransition_OnToOff_ReportsInactive()
    {
        var transition = HomeAssistantEventParser.TryReadMotionTransition(
            StateChangedFrame("binary_sensor.front_door_person", "on", "off"));

        Assert.Equal(new MotionTransition("binary_sensor.front_door_person", false), transition);
    }

    [Fact]
    public void TryReadMotionTransition_SameState_IsNotATransition()
    {
        // HA fires state_changed for attribute-only updates too. Treating one as
        // a transition would re-open a modal the user had just dismissed.
        Assert.Null(HomeAssistantEventParser.TryReadMotionTransition(
            StateChangedFrame("binary_sensor.front_door_person", "on", "on")));
        Assert.Null(HomeAssistantEventParser.TryReadMotionTransition(
            StateChangedFrame("binary_sensor.front_door_person", "off", "off")));
    }

    [Fact]
    public void TryReadMotionTransition_GoingUnavailableWhileOn_ReportsInactive()
    {
        // A sensor that drops off the network mid-detection reports unavailable
        // and never reports off. Reading that as "still detecting" would leave
        // the kiosk modal open until the camera came back.
        var transition = HomeAssistantEventParser.TryReadMotionTransition(
            StateChangedFrame("binary_sensor.front_door_person", "on", "unavailable"));

        Assert.Equal(new MotionTransition("binary_sensor.front_door_person", false), transition);
    }

    [Fact]
    public void TryReadMotionTransition_UnavailableToOn_ReportsActive()
    {
        var transition = HomeAssistantEventParser.TryReadMotionTransition(
            StateChangedFrame("binary_sensor.front_door_person", "unavailable", "on"));

        Assert.Equal(new MotionTransition("binary_sensor.front_door_person", true), transition);
    }

    [Fact]
    public void TryReadMotionTransition_UnavailableToUnknown_IsNotATransition()
    {
        // Both read as no-motion, so nothing crossed the line.
        Assert.Null(HomeAssistantEventParser.TryReadMotionTransition(
            StateChangedFrame("binary_sensor.front_door_person", "unavailable", "unknown")));
    }

    [Fact]
    public void TryReadMotionTransition_EntityAddedAlreadyOn_ReportsActive()
    {
        // old_state is null when HA has only just added the entity - a restart
        // that comes back with the sensor already detecting.
        var transition = HomeAssistantEventParser.TryReadMotionTransition(
            StateChangedFrame("binary_sensor.front_door_person", null, "on"));

        Assert.Equal(new MotionTransition("binary_sensor.front_door_person", true), transition);
    }

    [Fact]
    public void TryReadMotionTransition_EntityAddedOff_IsNotATransition()
    {
        Assert.Null(HomeAssistantEventParser.TryReadMotionTransition(
            StateChangedFrame("binary_sensor.front_door_person", null, "off")));
    }

    [Fact]
    public void TryReadMotionTransition_EntityRemovedWhileOn_ReportsInactive()
    {
        // new_state is null when the entity is removed from the registry.
        var transition = HomeAssistantEventParser.TryReadMotionTransition(
            StateChangedFrame("binary_sensor.front_door_person", "on", null));

        Assert.Equal(new MotionTransition("binary_sensor.front_door_person", false), transition);
    }

    [Fact]
    public void TryReadMotionTransition_OtherEventType_IsIgnored()
    {
        var frame = """
            {"id":1,"type":"event","event":{"event_type":"call_service","data":{"domain":"light","service":"turn_on"}}}
            """;

        Assert.Null(HomeAssistantEventParser.TryReadMotionTransition(frame));
    }

    [Theory]
    [InlineData("""{"type":"auth_required","ha_version":"2026.8.1"}""")]
    [InlineData("""{"type":"auth_ok","ha_version":"2026.8.1"}""")]
    [InlineData("""{"id":1,"type":"result","success":true,"result":null}""")]
    public void TryReadMotionTransition_HandshakeFrames_AreNotTransitions(string frame)
    {
        Assert.Null(HomeAssistantEventParser.TryReadMotionTransition(frame));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"type":"event","event":{"event_type":"state_changed","data":{}}}""")]
    [InlineData("""{"type":"event","event":{"event_type":"state_changed","data":{"old_state":{"state":"off"},"new_state":{"state":"on"}}}}""")]
    public void TryReadMotionTransition_MalformedFrames_AreIgnoredRatherThanThrown(string frame)
    {
        // Frames come off a socket, so a shape the parser doesn't recognize is
        // an expected input. It costs one dropped frame, never the listener.
        Assert.Null(HomeAssistantEventParser.TryReadMotionTransition(frame));
    }

    [Fact]
    public void ReadMessageType_ReturnsEnvelopeType()
    {
        Assert.Equal(HomeAssistantEventParser.AuthRequired,
            HomeAssistantEventParser.ReadMessageType("""{"type":"auth_required","ha_version":"2026.8.1"}"""));
        Assert.Equal(HomeAssistantEventParser.AuthInvalid,
            HomeAssistantEventParser.ReadMessageType("""{"type":"auth_invalid","message":"Invalid access token"}"""));
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("[]")]
    [InlineData("""{"ha_version":"2026.8.1"}""")]
    [InlineData("""{"type":7}""")]
    public void ReadMessageType_UnreadableFrame_ReturnsNull(string frame)
    {
        Assert.Null(HomeAssistantEventParser.ReadMessageType(frame));
    }
}

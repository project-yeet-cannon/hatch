using Hatch.Api.Ef;
using Hatch.Api.Services.DeviceMapping;

namespace Hatch.Api.Tests.DeviceMapping;

/// <summary>Covers DiscoveryService.InferKind - the pure entity-id-prefix branch that BuildSuggestion dispatches on. Camera is the newest branch; the others are re-asserted here to pin the precedence order (climate > media_player > camera > switch > light).</summary>
public class DiscoveryServiceTests
{
    [Fact]
    public void InferKind_CameraWithMotionSibling_ReturnsCameraAnchoredOnCameraEntity()
    {
        var match = DiscoveryService.InferKind(["camera.front_door", "binary_sensor.front_door_motion"]);

        Assert.Equal(new DiscoveryService.KindMatch(DeviceKind.Camera, "camera.front_door"), match);
    }

    [Fact]
    public void InferKind_CameraWithoutMotionSibling_StillReturnsCamera()
    {
        var match = DiscoveryService.InferKind(["camera.front_door"]);

        Assert.Equal(new DiscoveryService.KindMatch(DeviceKind.Camera, "camera.front_door"), match);
    }

    [Fact]
    public void InferKind_MultipleCameraProfiles_AnchorsOnSubStreamNotOrdinalFirst()
    {
        // The five camera.* entities a Reolink device publishes. Ordinal-first is
        // _balanced, which is H.265 and won't play in a browser; _fluent is the
        // H.264 sub-stream. _snapshots_fluent also ends in _fluent but is stills.
        var match = DiscoveryService.InferKind([
            "camera.front_door_balanced",
            "camera.front_door_clear",
            "camera.front_door_fluent",
            "camera.front_door_snapshots_clear",
            "camera.front_door_snapshots_fluent",
            "binary_sensor.front_door_motion",
        ]);

        Assert.Equal(new DiscoveryService.KindMatch(DeviceKind.Camera, "camera.front_door_fluent"), match);
    }

    [Fact]
    public void InferKind_MultipleCameraProfiles_AnchorIsIndependentOfInputOrder()
    {
        // BuildSuggestion sorts before calling, but the anchor must not depend on
        // it: enabling a disabled profile entity must not move the anchor.
        var match = DiscoveryService.InferKind([
            "camera.front_door_snapshots_fluent",
            "camera.front_door_fluent",
            "camera.front_door_balanced",
        ]);

        Assert.Equal(new DiscoveryService.KindMatch(DeviceKind.Camera, "camera.front_door_fluent"), match);
    }

    [Fact]
    public void InferKind_NoSubStreamProfile_FallsBackToOrdinalFirstCamera()
    {
        // A non-Reolink camera naming its entities anything else still imports.
        var match = DiscoveryService.InferKind(["camera.front_door_hd", "camera.front_door_sd"]);

        Assert.Equal(new DiscoveryService.KindMatch(DeviceKind.Camera, "camera.front_door_hd"), match);
    }

    [Fact]
    public void InferKind_OnlySnapshotProfiles_StillReturnsCamera()
    {
        // _snapshots_fluent is excluded from the sub-stream preference, not from
        // the fallback - a group with nothing else should still import.
        var match = DiscoveryService.InferKind(["camera.front_door_snapshots_clear", "camera.front_door_snapshots_fluent"]);

        Assert.Equal(new DiscoveryService.KindMatch(DeviceKind.Camera, "camera.front_door_snapshots_clear"), match);
    }

    [Fact]
    public void InferKind_UnrecognizedEntities_ReturnsNull()
    {
        var match = DiscoveryService.InferKind(["sensor.outdoor_temperature", "sensor.outdoor_humidity"]);

        Assert.Null(match);
    }

    [Fact]
    public void InferKind_RealReolinkDevice_IsACameraNotASmartSwitch()
    {
        // The entity list of the first physical camera (HA device
        // 6bec71b762f35cdf12246606b5a3ecdb, 2026-08-23). Its six switch.* entities
        // are camera settings - record, record audio, infrared lights, FTP upload,
        // email on event, push notifications - not a smart switch. With switch
        // checked first this group imported as SmartSwitch anchored on
        // switch.innit_email_on_event, so no CameraFeed channel was ever built and
        // nothing downstream of Phase 2 could run.
        var match = DiscoveryService.InferKind(ReolinkDeviceEntityIds);

        Assert.Equal(new DiscoveryService.KindMatch(DeviceKind.Camera, "camera.innit_fluent"), match);
    }

    [Fact]
    public void InferKind_CameraTakesPrecedenceOverAFloodlightSibling()
    {
        // The spotlight models (RLC-811A, RLC-1224A) publish light.*_floodlight,
        // which anchors the group as a Light for the same reason switch did.
        var match = DiscoveryService.InferKind([
            "camera.driveway_fluent",
            "light.driveway_floodlight",
            "binary_sensor.driveway_motion",
        ]);

        Assert.Equal(new DiscoveryService.KindMatch(DeviceKind.Camera, "camera.driveway_fluent"), match);
    }

    [Fact]
    public void InferKind_SwitchWithNoCameraSibling_IsStillSmartSwitch()
    {
        // Moving camera ahead of switch must not cost the switch branch anything.
        var match = DiscoveryService.InferKind(["switch.garage_light", "sensor.garage_power"]);

        Assert.Equal(new DiscoveryService.KindMatch(DeviceKind.SmartSwitch, "switch.garage_light"), match);
    }

    [Fact]
    public void CameraChannelBuilder_RealReolinkDevice_AnchorsMotionOnThePersonSensor()
    {
        // Same device. Its AI sensors are _person/_vehicle/_animal (not the _pet
        // the hardware notes predicted), so the _person preference holds and the
        // noisy plain _motion sensor stays unmapped.
        var channels = CameraChannelBuilder.Build("camera.innit_fluent", ReolinkDeviceEntityIds);

        Assert.Collection(channels,
            c => Assert.Equal((DeviceChannelMetric.CameraFeed, "camera.innit_fluent", (string?)null), (c.Metric, c.HaEntityId, c.HaAttribute)),
            c => Assert.Equal((DeviceChannelMetric.MotionState, "binary_sensor.innit_person", (string?)null), (c.Metric, c.HaEntityId, c.HaAttribute)));
    }

    /// <summary>Every entity Home Assistant published for the first physical camera, verbatim.</summary>
    private static readonly string[] ReolinkDeviceEntityIds =
    [
        "binary_sensor.innit_animal",
        "binary_sensor.innit_motion",
        "binary_sensor.innit_person",
        "binary_sensor.innit_vehicle",
        "camera.innit_fluent",
        "number.innit_ai_animal_sensitivity",
        "number.innit_ai_person_sensitivity",
        "number.innit_ai_vehicle_sensitivity",
        "number.innit_motion_sensitivity",
        "select.innit_day_night_mode",
        "sensor.innit_day_night_state",
        "switch.innit_email_on_event",
        "switch.innit_ftp_upload",
        "switch.innit_infrared_lights_in_night_mode",
        "switch.innit_push_notifications",
        "switch.innit_record",
        "switch.innit_record_audio",
        "update.innit_firmware",
    ];

    [Fact]
    public void CameraChannelBuilder_WithMotionSibling_EmitsFeedAndMotionChannels()
    {
        var channels = CameraChannelBuilder.Build("camera.front_door", ["camera.front_door", "binary_sensor.front_door_motion"]);

        Assert.Collection(channels,
            c => Assert.Equal((DeviceChannelMetric.CameraFeed, "camera.front_door", (string?)null), (c.Metric, c.HaEntityId, c.HaAttribute)),
            c => Assert.Equal((DeviceChannelMetric.MotionState, "binary_sensor.front_door_motion", (string?)null), (c.Metric, c.HaEntityId, c.HaAttribute)));
    }

    [Fact]
    public void CameraChannelBuilder_WithPersonSibling_PrefersPersonOverMotion()
    {
        // On-camera AI detection: _person fires far less often than the plain
        // _motion sensor, and every transition opens a modal on the kiosk.
        var channels = CameraChannelBuilder.Build("camera.front_door_fluent", [
            "camera.front_door_fluent",
            "binary_sensor.front_door_motion",
            "binary_sensor.front_door_person",
            "binary_sensor.front_door_vehicle",
        ]);

        Assert.Collection(channels,
            c => Assert.Equal((DeviceChannelMetric.CameraFeed, "camera.front_door_fluent", (string?)null), (c.Metric, c.HaEntityId, c.HaAttribute)),
            c => Assert.Equal((DeviceChannelMetric.MotionState, "binary_sensor.front_door_person", (string?)null), (c.Metric, c.HaEntityId, c.HaAttribute)));
    }

    [Fact]
    public void CameraChannelBuilder_WithPersonSibling_PrefersPersonRegardlessOfInputOrder()
    {
        var channels = CameraChannelBuilder.Build("camera.front_door_fluent", [
            "binary_sensor.front_door_person",
            "binary_sensor.front_door_motion",
        ]);

        Assert.Equal("binary_sensor.front_door_person", channels[1].HaEntityId);
    }

    [Fact]
    public void CameraChannelBuilder_WithoutPersonSibling_FallsBackToMotion()
    {
        // A camera with no AI detection only ever publishes _motion.
        var channels = CameraChannelBuilder.Build("camera.driveway", ["camera.driveway", "binary_sensor.driveway_motion"]);

        Assert.Collection(channels,
            c => Assert.Equal(DeviceChannelMetric.CameraFeed, c.Metric),
            c => Assert.Equal((DeviceChannelMetric.MotionState, "binary_sensor.driveway_motion"), (c.Metric, c.HaEntityId)));
    }

    [Fact]
    public void CameraChannelBuilder_WithUnrelatedBinarySensors_EmitsOnlyFeedChannel()
    {
        // Cameras carry other binary_sensor.* siblings too; none of them is a
        // motion source, so nothing should be mapped to MotionState.
        var channels = CameraChannelBuilder.Build("camera.driveway", ["camera.driveway", "binary_sensor.driveway_sd_card"]);

        var channel = Assert.Single(channels);
        Assert.Equal(DeviceChannelMetric.CameraFeed, channel.Metric);
    }

    [Fact]
    public void CameraChannelBuilder_WithoutMotionSibling_EmitsOnlyFeedChannel()
    {
        var channels = CameraChannelBuilder.Build("camera.front_door", ["camera.front_door"]);

        var channel = Assert.Single(channels);
        Assert.Equal(DeviceChannelMetric.CameraFeed, channel.Metric);
        Assert.Equal("camera.front_door", channel.HaEntityId);
    }

    [Fact]
    public void SwitchChannelBuilder_WithLightSibling_EmitsPowerStateForBoth()
    {
        var channels = SwitchChannelBuilder.Build(
            "switch.garage_motion_detection",
            ["switch.garage_motion_detection", "light.garage_light", "siren.garage_siren"]);

        Assert.Collection(channels,
            c => Assert.Equal((DeviceChannelMetric.PowerState, "switch.garage_motion_detection", (string?)null), (c.Metric, c.HaEntityId, c.HaAttribute)),
            c => Assert.Equal((DeviceChannelMetric.PowerState, "light.garage_light", (string?)null), (c.Metric, c.HaEntityId, c.HaAttribute)));
    }

    [Fact]
    public void SwitchChannelBuilder_WithoutLightSibling_EmitsOnlySwitchChannel()
    {
        var channels = SwitchChannelBuilder.Build("switch.living_room_fan", ["switch.living_room_fan"]);

        var channel = Assert.Single(channels);
        Assert.Equal(DeviceChannelMetric.PowerState, channel.Metric);
        Assert.Equal("switch.living_room_fan", channel.HaEntityId);
    }
}

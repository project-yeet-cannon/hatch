using Aerie.Api.Ef;
using Aerie.Api.Services.DeviceMapping;

namespace Aerie.Api.Tests.DeviceMapping;

/// <summary>Covers DiscoveryService.InferKind - the pure entity-id-prefix branch that BuildSuggestion dispatches on. Camera is the newest branch; the others are re-asserted here to pin the existing precedence order (climate > media_player > switch > light > camera).</summary>
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
    public void InferKind_SwitchTakesPrecedenceOverCamera_WhenBothPresent()
    {
        // Not an expected real-world grouping, but pins that switch is checked before camera.
        var match = DiscoveryService.InferKind(["switch.garage_light", "camera.front_door"]);

        Assert.Equal(new DiscoveryService.KindMatch(DeviceKind.SmartSwitch, "switch.garage_light"), match);
    }

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

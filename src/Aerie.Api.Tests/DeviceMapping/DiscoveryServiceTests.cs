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
    public void CameraChannelBuilder_WithoutMotionSibling_EmitsOnlyFeedChannel()
    {
        var channels = CameraChannelBuilder.Build("camera.front_door", ["camera.front_door"]);

        var channel = Assert.Single(channels);
        Assert.Equal(DeviceChannelMetric.CameraFeed, channel.Metric);
        Assert.Equal("camera.front_door", channel.HaEntityId);
    }
}

using Aerie.Api.Ef;
using Aerie.Api.Services.DeviceMapping;
using Aerie.Api.Services.Routines;

namespace Aerie.Api.Tests.Routines;

/// <summary>Covers RoutineActionExecutor.ExecuteAsync's dispatch/ordering/value-parsing against a fake IHomeAssistantCommandService.</summary>
public class RoutineActionExecutorTests
{
    private const string BaseUrl = "https://home.example.com/media";

    private static EfRoutineAction Action(RoutineActionKind kind, string? value, int sortOrder, string entityId = "switch.test") =>
        new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Value = value,
            SortOrder = sortOrder,
            Channel = new EfDeviceChannel { HaEntityId = entityId, Metric = DeviceChannelMetric.PowerState },
        };

    [Fact]
    public async Task ExecuteAsync_RunsActions_InSortOrder_NotDeclarationOrder()
    {
        var fake = new FakeCommandService();
        var actions = new[]
        {
            Action(RoutineActionKind.SetPower, "true", sortOrder: 2, entityId: "switch.second"),
            Action(RoutineActionKind.SetPower, "true", sortOrder: 0, entityId: "switch.first"),
            Action(RoutineActionKind.SetPower, "true", sortOrder: 1, entityId: "switch.middle"),
        };

        await RoutineActionExecutor.ExecuteAsync(actions, fake, BaseUrl, CancellationToken.None);

        Assert.Equal(["switch.first", "switch.middle", "switch.second"], fake.Calls.Select(c => c.EntityId));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    [InlineData("not-a-bool", false)]
    public async Task ExecuteAsync_SetPower_ParsesValue(string? value, bool expectedOn)
    {
        var fake = new FakeCommandService();
        await RoutineActionExecutor.ExecuteAsync([Action(RoutineActionKind.SetPower, value, 0)], fake, BaseUrl, CancellationToken.None);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("SetPower", call.Method);
        Assert.Equal(expectedOn, call.PowerOn);
    }

    [Fact]
    public async Task ExecuteAsync_SetTemperature_ParsesDecimalValue()
    {
        var fake = new FakeCommandService();
        await RoutineActionExecutor.ExecuteAsync([Action(RoutineActionKind.SetTemperature, "68.5", 0)], fake, BaseUrl, CancellationToken.None);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("SetTemperature", call.Method);
        Assert.Equal(68.5m, call.Temperature);
    }

    [Fact]
    public async Task ExecuteAsync_SetTemperature_MissingValue_Throws()
    {
        var fake = new FakeCommandService();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RoutineActionExecutor.ExecuteAsync([Action(RoutineActionKind.SetTemperature, null, 0)], fake, BaseUrl, CancellationToken.None));
    }

    [Theory]
    [InlineData(RoutineActionKind.SetHvacMode, "SetHvacMode")]
    [InlineData(RoutineActionKind.SetFanMode, "SetFanMode")]
    public async Task ExecuteAsync_ModeActions_PassModeStringThrough(RoutineActionKind kind, string expectedMethod)
    {
        var fake = new FakeCommandService();
        await RoutineActionExecutor.ExecuteAsync([Action(kind, "auto", 0)], fake, BaseUrl, CancellationToken.None);

        var call = Assert.Single(fake.Calls);
        Assert.Equal(expectedMethod, call.Method);
        Assert.Equal("auto", call.Mode);
    }

    [Fact]
    public async Task ExecuteAsync_TriggerScene_IgnoresValue()
    {
        var fake = new FakeCommandService();
        await RoutineActionExecutor.ExecuteAsync([Action(RoutineActionKind.TriggerScene, null, 0, "scene.night")], fake, BaseUrl, CancellationToken.None);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("TriggerScene", call.Method);
        Assert.Equal("scene.night", call.EntityId);
    }

    [Fact]
    public async Task ExecuteAsync_PlayMedia_ResolvesStoredPathAgainstBaseUrl()
    {
        var fake = new FakeCommandService();
        var action = Action(RoutineActionKind.PlayMedia, "Miles Davis/So What.flac", 0, "media_player.deck");

        await RoutineActionExecutor.ExecuteAsync([action], fake, BaseUrl, CancellationToken.None);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("PlayMedia", call.Method);
        Assert.Equal("media_player.deck", call.EntityId);
        Assert.Equal("https://home.example.com/media/Miles%20Davis/So%20What.flac", call.MediaContentId);
        Assert.Equal("music", call.MediaContentType);
    }

    [Fact]
    public async Task ExecuteAsync_PlayMedia_PassesThroughAnAlreadyResolvableId()
    {
        var fake = new FakeCommandService();
        var action = Action(RoutineActionKind.PlayMedia, "media-source://media_source/local/x.mp3", 0, "media_player.deck");

        await RoutineActionExecutor.ExecuteAsync([action], fake, mediaLibraryBaseUrl: null, CancellationToken.None);

        Assert.Equal("media-source://media_source/local/x.mp3", Assert.Single(fake.Calls).MediaContentId);
    }

    [Fact]
    public async Task ExecuteAsync_PlayMedia_UnresolvablePath_ThrowsWithoutCallingHa()
    {
        var fake = new FakeCommandService();
        var action = Action(RoutineActionKind.PlayMedia, "Miles Davis/So What.flac", 0, "media_player.deck");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RoutineActionExecutor.ExecuteAsync([action], fake, mediaLibraryBaseUrl: null, CancellationToken.None));

        Assert.Contains("MediaLibraryBaseUrl", ex.Message);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_ActionWithoutLoadedChannel_Throws()
    {
        var action = new EfRoutineAction { Id = Guid.NewGuid(), Kind = RoutineActionKind.TriggerScene, SortOrder = 0, Channel = null };
        var fake = new FakeCommandService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RoutineActionExecutor.ExecuteAsync([action], fake, BaseUrl, CancellationToken.None));
    }

    private sealed record Call(
        string Method, string EntityId, bool PowerOn = false, decimal? Temperature = null, string? Mode = null,
        string? MediaContentId = null, string? MediaContentType = null);

    private sealed class FakeCommandService : IHomeAssistantCommandService
    {
        public List<Call> Calls { get; } = [];

        public Task SetPowerAsync(string entityId, bool on)
        {
            Calls.Add(new Call("SetPower", entityId, PowerOn: on));
            return Task.CompletedTask;
        }

        public Task SetTemperatureAsync(string entityId, decimal temperature)
        {
            Calls.Add(new Call("SetTemperature", entityId, Temperature: temperature));
            return Task.CompletedTask;
        }

        public Task SetHvacModeAsync(string entityId, string mode)
        {
            Calls.Add(new Call("SetHvacMode", entityId, Mode: mode));
            return Task.CompletedTask;
        }

        public Task SetFanModeAsync(string entityId, string mode)
        {
            Calls.Add(new Call("SetFanMode", entityId, Mode: mode));
            return Task.CompletedTask;
        }

        public Task TriggerSceneAsync(string entityId)
        {
            Calls.Add(new Call("TriggerScene", entityId));
            return Task.CompletedTask;
        }

        public Task PlayMediaAsync(string entityId, string mediaContentId, string mediaContentType)
        {
            Calls.Add(new Call("PlayMedia", entityId, MediaContentId: mediaContentId, MediaContentType: mediaContentType));
            return Task.CompletedTask;
        }
    }
}

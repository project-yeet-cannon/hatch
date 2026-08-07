using Aerie.Api.Ef;
using Aerie.Api.Services.Routines;

namespace Aerie.Api.Tests.Routines;

/// <summary>Covers RoutineCommandMapper's ordering and kind translation. Value parsing and media resolution moved to ClimateCommandService/CommandExpectation, so they're tested there rather than here.</summary>
public class RoutineCommandMapperTests
{
    private static EfRoutineAction Action(RoutineActionKind kind, string? value, int sortOrder, Guid? channelId = null) =>
        new() { Id = Guid.NewGuid(), ChannelId = channelId ?? Guid.NewGuid(), Kind = kind, Value = value, SortOrder = sortOrder };

    [Fact]
    public void ToCommandRequests_OrdersBySortOrder_NotDeclarationOrder()
    {
        var second = Guid.NewGuid();
        var first = Guid.NewGuid();
        var middle = Guid.NewGuid();

        var requests = RoutineCommandMapper.ToCommandRequests(
        [
            Action(RoutineActionKind.SetPower, "true", 2, second),
            Action(RoutineActionKind.SetPower, "true", 0, first),
            Action(RoutineActionKind.SetPower, "true", 1, middle),
        ], "Routine 'Night mode'");

        Assert.Equal([first, middle, second], requests.Select(r => r.ChannelId));
    }

    [Fact]
    public void ToCommandRequests_TagsEveryRequestAsRoutineSourced()
    {
        var requests = RoutineCommandMapper.ToCommandRequests(
            [Action(RoutineActionKind.SetTemperature, "68", 0)], "Routine 'Night mode'");

        var request = Assert.Single(requests);
        Assert.Equal(CommandSource.Routine, request.Source);
        Assert.Equal("Routine 'Night mode'", request.Reason);
        Assert.Equal("68", request.Value);
    }

    [Theory]
    [InlineData(RoutineActionKind.SetPower, CommandKind.SetPower)]
    [InlineData(RoutineActionKind.SetTemperature, CommandKind.SetTemperature)]
    [InlineData(RoutineActionKind.SetHvacMode, CommandKind.SetHvacMode)]
    [InlineData(RoutineActionKind.SetFanMode, CommandKind.SetFanMode)]
    [InlineData(RoutineActionKind.TriggerScene, CommandKind.TriggerScene)]
    [InlineData(RoutineActionKind.PlayMedia, CommandKind.PlayMedia)]
    public void ToCommandKind_MapsEveryRoutineActionKind(RoutineActionKind kind, CommandKind expected) =>
        Assert.Equal(expected, RoutineCommandMapper.ToCommandKind(kind));
}

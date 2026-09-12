using Hatch.Api.Ef;
using Hatch.Api.Services;
using HADotNet.Core.Models;

namespace Hatch.Api.Tests;

public class ChannelValueExtractorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private static EfDeviceChannel Channel(string? haAttribute) =>
        new() { HaEntityId = "sensor.test", HaAttribute = haAttribute };

    private static StateObject State(string state, Dictionary<string, object>? attributes = null) =>
        new() { State = state, Attributes = attributes ?? [], LastUpdated = Now };

    [Fact]
    public void Extract_BareState_NumericStringReturnsDecimal()
    {
        var (numeric, text) = ChannelValueExtractor.Extract(State("72.5"), Channel(haAttribute: null));

        Assert.Equal(72.5m, numeric);
        Assert.Null(text);
    }

    [Fact]
    public void Extract_BareState_NonNumericStringReturnsText()
    {
        var (numeric, text) = ChannelValueExtractor.Extract(State("unavailable"), Channel(haAttribute: null));

        Assert.Null(numeric);
        Assert.Equal("unavailable", text);
    }

    [Fact]
    public void Extract_AttributeLongValue_ReturnsDecimal()
    {
        var state = State("heat", new Dictionary<string, object> { ["current_temperature"] = 72L });

        var (numeric, text) = ChannelValueExtractor.Extract(state, Channel("current_temperature"));

        Assert.Equal(72m, numeric);
        Assert.Null(text);
    }

    [Fact]
    public void Extract_AttributeDoubleValue_ReturnsDecimal()
    {
        var state = State("heat", new Dictionary<string, object> { ["current_temperature"] = 72.5d });

        var (numeric, text) = ChannelValueExtractor.Extract(state, Channel("current_temperature"));

        Assert.Equal(72.5m, numeric);
        Assert.Null(text);
    }

    [Fact]
    public void Extract_AttributeDecimalValue_ReturnsDecimal()
    {
        var state = State("heat", new Dictionary<string, object> { ["current_temperature"] = 72.5m });

        var (numeric, text) = ChannelValueExtractor.Extract(state, Channel("current_temperature"));

        Assert.Equal(72.5m, numeric);
        Assert.Null(text);
    }

    [Fact]
    public void Extract_AttributeNonNumericString_ReturnsText()
    {
        var state = State("heat", new Dictionary<string, object> { ["hvac_action"] = "heating" });

        var (numeric, text) = ChannelValueExtractor.Extract(state, Channel("hvac_action"));

        Assert.Null(numeric);
        Assert.Equal("heating", text);
    }

    [Fact]
    public void Extract_MissingAttribute_ReturnsNullNull()
    {
        var state = State("heat", new Dictionary<string, object> { ["hvac_action"] = "heating" });

        var (numeric, text) = ChannelValueExtractor.Extract(state, Channel("current_temperature"));

        Assert.Null(numeric);
        Assert.Null(text);
    }

    [Fact]
    public void Extract_NonStringNonNumericAttribute_FallsBackToToString()
    {
        var state = State("heat", new Dictionary<string, object> { ["away_mode"] = true });

        var (numeric, text) = ChannelValueExtractor.Extract(state, Channel("away_mode"));

        Assert.Null(numeric);
        Assert.Equal("True", text);
    }
}

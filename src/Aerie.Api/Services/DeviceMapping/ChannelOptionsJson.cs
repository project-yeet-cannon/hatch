using System.Text.Json;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>(De)serializes EfDeviceChannel.AvailableOptions, which stores a mode channel's legal values as a single JSON-array-of-strings column.</summary>
public static class ChannelOptionsJson
{
    public static string? Serialize(IReadOnlyList<string>? options) =>
        options is null ? null : JsonSerializer.Serialize(options);

    public static IReadOnlyList<string>? Deserialize(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<List<string>>(json);
}

using System.Globalization;
using Aerie.Api.Ef;
using HADotNet.Core.Models;

namespace Aerie.Api.Services;

/// <summary>
/// Pure state -> value mapping - no DB, no HA client, no clock. Kept separate
/// so it can be unit-tested directly (see Aerie.Api.Tests), same convention
/// as Services/Dashboard/ZoneMath.
/// </summary>
public static class ChannelValueExtractor
{
    /// <summary>
    /// Reads a channel's value out of a historical HA state: the bare state
    /// string when HaAttribute is null (hygrometer entities), or the named
    /// attribute (thermostat entities) otherwise. HA numbers arrive boxed as
    /// long/double; anything that doesn't parse as a decimal (e.g.
    /// hvac_action's "heating"/"idle") is returned as text instead.
    /// </summary>
    public static (decimal? Numeric, string? Text) Extract(StateObject state, EfDeviceChannel channel)
    {
        object? raw = channel.HaAttribute is null
            ? state.State
            : state.Attributes.GetValueOrDefault(channel.HaAttribute);

        return raw switch
        {
            null => (null, null),
            long l => ((decimal)l, null),
            double d => ((decimal)d, null),
            decimal m => (m, null),
            string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => (parsed, null),
            string s => (null, s),
            _ => (null, raw.ToString())
        };
    }
}

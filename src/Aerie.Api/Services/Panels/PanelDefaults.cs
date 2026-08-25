namespace Aerie.Api.Services.Panels;

/// <summary>
/// Fallback thermostat bounds for a Control whose MinF/MaxF/StepF are null.
///
/// Constants rather than settings because the only knob that has ever needed to
/// differ is per-control, and EfPanelItem already carries it: an air
/// conditioner and a radiator on the same wall want different ranges, so a
/// single house-wide setting would be the wrong shape anyway. These are the
/// "an admin left it blank" answer for a Fahrenheit household, not a policy.
/// </summary>
public static class PanelDefaults
{
    public const decimal MinF = 60m;
    public const decimal MaxF = 85m;
    public const decimal StepF = 1m;
}

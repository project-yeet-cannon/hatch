namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// Deploy-time config for Hatch (the "Hatch" appsettings section), shaped like
/// <see cref="Aerie.Api.Services.Auth.AuthOptions"/> and bound by
/// <see cref="HatchModule"/> - a module owns its own registrations
/// (Modules/README.md).
/// </summary>
/// <remarks>
/// Nothing here is written into appsettings.json. The default below is the
/// value, and a section that only ever restated it would be a second place for
/// it to drift.
/// </remarks>
public class HatchOptions
{
    public const string SectionName = "Hatch";

    /// <summary>
    /// How long a claim survives without a heartbeat - see
    /// <see cref="EfHatchIssue.ClaimToken"/>. Long enough that an increment
    /// pausing on a slow build does not lose its lease, short enough that a
    /// runner killed at the wall clock frees its ticket before anybody is
    /// waiting on it.
    /// </summary>
    /// <remarks>
    /// Read through <see cref="IssueClaims.TtlSeconds"/> and nowhere else, so
    /// the guard against a value that would make every claim dead on arrival
    /// lives in one place. Returned to the client in the claim response rather
    /// than configured on both sides: the server honours it, so the server says
    /// what it is.
    /// </remarks>
    public int ClaimTtlSeconds { get; set; } = 300;
}

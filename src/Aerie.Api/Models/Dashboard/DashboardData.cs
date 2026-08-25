using Aerie.Api.Models.Panels;

namespace Aerie.Api.Models.Dashboard;

// These records are the server side of the dashboard's data contract. They must
// serialize to exactly the shape in Aerie.Web/apps/dashboard/src/types.ts (camelCase
// keys, ISO-8601 timestamps - both the System.Text.Json defaults). Only raw
// physical quantities live here; all presentation (status words, badge text) is
// derived on the client. Nullable numeric fields (CurrentTempF, HumidityPct,
// Low, High) mean "no reading yet" - distinct from a real 0 - so the client
// can render a placeholder instead of a false zero.

public record TempPoint(DateTimeOffset Time, decimal TempF);

public record ComfortRange(decimal LowF, decimal HighF);

public record DailyExtreme(decimal TempF, DateTimeOffset Time);

public record ZoneClimate(
    string Id,
    string Name,
    decimal? CurrentTempF,
    ComfortRange ComfortRange,
    IReadOnlyList<TempPoint> History,
    IReadOnlyList<TempPoint> Forecast,
    DailyExtreme? Low,
    DailyExtreme? High);

public record Precipitation(decimal AmountIn, string Window);

public record HourlyOutside(
    DateTimeOffset Time,
    decimal HumidityPct,
    decimal CloudCoverPct,
    decimal PrecipIn);

public record OutsideClimate(
    decimal? CurrentTempF,
    decimal? HumidityPct,
    decimal SunHoursRemaining,
    DateTimeOffset SunsetTime,
    Precipitation Precipitation,
    string Note,
    IReadOnlyList<TempPoint> History,
    IReadOnlyList<TempPoint> Forecast,
    IReadOnlyList<HourlyOutside> Hourly);

/// <summary>
/// The four sun events (see <see cref="Aerie.Api.Services.Dashboard.SolarCalculator.EventsForDay"/>)
/// that bound the dashboard's circadian theme phases: full light (Sunrise-Sunset),
/// evening transition (Sunset-Dusk), full dark (Dusk-Dawn), morning transition (Dawn-Sunrise).
/// </summary>
public record SunEvents(DateTimeOffset Dawn, DateTimeOffset Sunrise, DateTimeOffset Sunset, DateTimeOffset Dusk);

/// <summary>
/// A Routine as shown on the kiosk - just enough to render a tap-to-trigger
/// button; see RoutinesController for the full admin-editable shape.
/// IsActive is null for a momentary (non-toggle) routine, and for a toggle
/// routine reflects whether every SetPower action's channel currently reads
/// "on" - see RoutineService.
/// </summary>
public record RoutineSummary(Guid Id, string Name, string? Description, string? Icon, string? Color, bool IsToggle, bool? IsActive);

/// <summary>
/// One camera as the kiosk's button row renders it
/// (docs/camera-devices-architecture.md) - a name to put under the tile, and an
/// id to open the stream with.
///
/// <paramref name="IsConfigured"/> is whether this camera has an address to
/// stream from yet, which is the same question CameraController asks before it
/// answers 409. It is carried here so the kiosk can say "not set up yet"
/// instead of "unavailable" without a second call: a failed WebSocket handshake
/// reaches a browser with no status code on it, so the modal cannot tell the
/// two apart on its own.
/// </summary>
public record CameraSummary(Guid Id, string Name, bool IsConfigured);

/// <summary>
/// One cached calendar event as the kiosk agenda renders it. Color is the
/// calendar's ColorOverride when the admin set one, otherwise the provider's
/// own color - the client never sees which of the two it got.
/// </summary>
public record CalendarEventSummary(
    Guid Id,
    string CalendarName,
    string? Color,
    string Title,
    string? Location,
    bool IsAllDay,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

/// <summary>
/// One local calendar day in the agenda window. Days with no events are still
/// present, so the kiosk can say "nothing tomorrow" rather than collapsing the
/// section and leaving it ambiguous whether tomorrow is empty or unsynced.
/// DateOnly serializes as "2026-08-21", which is the grouping key the client wants.
/// </summary>
public record CalendarDay(DateOnly Date, IReadOnlyList<CalendarEventSummary> Events);

/// <summary>Which half of the outdoor hazard feature produced an alert. The kiosk may treat the two differently; nothing else depends on it.</summary>
public enum HazardKind { Weather, AirQuality }

/// <summary>
/// One thing outside worth saying out loud, in Aerie's vocabulary rather than
/// any provider's: a watch/warning/advisory as issued, or the single synthetic
/// alert bad air produces (see HazardService).
///
/// <paramref name="Severity"/> is a string rather than the stored enum because
/// air quality has no NWS severity to report - both halves are normalized onto
/// the one vocabulary "Unknown" | "Minor" | "Moderate" | "Severe" | "Extreme",
/// which is all the client needs to decide how loud to be.
/// </summary>
/// <param name="Id">Stable within a snapshot, for keying a list. A weather alert's row id; the constant "air-quality" for the synthetic one, of which there is at most one.</param>
/// <param name="StartsAt">When it takes effect, or when a forecast peak arrives. Null reads as "already in effect".</param>
/// <param name="EndsAt">When it stops applying; null when the provider gave no end.</param>
public record HazardAlert(
    string Id,
    HazardKind Kind,
    string Severity,
    string Title,
    string? Detail,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt);

public record DashboardData(
    DateTimeOffset GeneratedAt,
    string Timezone,
    IReadOnlyList<ZoneClimate> Zones,
    OutsideClimate Outside,
    SunEvents SunEvents,
    IReadOnlyList<RoutineSummary> Routines,
    IReadOnlyList<CameraSummary> Cameras,
    IReadOnlyList<PanelSummary> Panels,
    IReadOnlyList<CalendarDay> Calendar,
    IReadOnlyList<HazardAlert> Alerts);

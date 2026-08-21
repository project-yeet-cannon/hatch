namespace Aerie.Api.Models.Calendar;

// The API-facing shapes for the connected-account / calendar domain, kept
// separate from the Ef* entities so the admin app has a stable contract
// independent of storage (same rationale as Models/Routines/Dtos.cs).
//
// Note what is *not* here: RefreshToken, AccessToken and AccessTokenExpiresAt.
// Token material never leaves the database, so no field on these records can
// carry it out - the omission is the mechanism, not an oversight.

/// <param name="ProviderColor">The color the provider reports. <paramref name="ColorOverride"/> wins when set; the admin page shows both so a recolor can be undone back to the provider's.</param>
public record CalendarDto(
    Guid Id,
    string ProviderCalendarId,
    string Name,
    string? ProviderColor,
    string? ColorOverride,
    bool Included,
    int SortOrder,
    string? TimeZone,
    bool IsPrimary);

/// <param name="NeedsReauth">The account's grant is dead and only re-consent fixes it. The row stays in the list precisely so the page can offer "Reconnect".</param>
public record CalendarAccountDto(
    Guid Id,
    string Provider,
    string AccountEmail,
    string? DisplayName,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? LastSyncedAt,
    string? LastSyncError,
    bool NeedsReauth,
    bool Enabled,
    IReadOnlyList<CalendarDto> Calendars);

/// <summary>
/// The admin-owned half of a calendar - everything a refresh from the provider
/// deliberately leaves alone (see CalendarDiscoveryService).
/// </summary>
/// <param name="ColorOverride">A CSS hex color (<c>#rgb</c> or <c>#rrggbb</c>), or null to fall back to the provider's. Validated on write because it reaches the kiosk as a style value.</param>
public record CalendarVisibilityRequest(bool Included, string? ColorOverride, int SortOrder);

/// <summary>What a "Refresh calendars" run changed, so the admin page can say so rather than just re-rendering.</summary>
public record CalendarDiscoveryDto(int Added, int Updated, int Removed);

/// <summary>
/// What an on-demand sync run did. Accounts that failed are a count, not a
/// list: each one's message is on its own <see cref="CalendarAccountDto"/> as
/// <c>LastSyncError</c>, which is where the page already reads it from.
/// </summary>
public record CalendarSyncDto(int Accounts, int Calendars, int Written, int Removed, int FailedAccounts);

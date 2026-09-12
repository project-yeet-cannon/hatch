using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Ef;

/// <summary>
/// The values EfCalendarAccount.Provider takes. A string rather than an enum
/// so a second provider is a new constant, not a migration - this is the one
/// place the spelling is decided.
/// </summary>
public static class CalendarProviders
{
    public const string Google = "google";
}

/// <summary>
/// One connected calendar provider account - today always a Google account an
/// admin authorized through CalendarOAuthController. Holds the OAuth grant;
/// the calendars it can see hang off it as EfCalendar rows.
///
/// Provider is a string rather than an enum so a second provider (CalDAV,
/// Microsoft) is a new value, not a migration - see DeviceChannelMetric for
/// why the enums here are append-only in the first place.
/// </summary>
[Table("CalendarAccounts")]
[Index(nameof(Provider), nameof(AccountEmail), IsUnique = true)]
public class EfCalendarAccount
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>Provider slug, currently always "google".</summary>
    public required string Provider { get; set; }

    /// <summary>The account's email as resolved from the OAuth id token - the natural key an admin recognizes, and what re-connecting the same account upserts on.</summary>
    public required string AccountEmail { get; set; }

    /// <summary>Optional friendlier label for the admin list ("Work", "Nathan"). Null falls back to AccountEmail.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The long-lived OAuth refresh token, stored protected (SecretProtector scheme v1 - obfuscation, not encryption; see docs/secrets-architecture.md).</summary>
    public required string RefreshToken { get; set; }

    /// <summary>The current access token, stored obfuscated. Null until GoogleTokenProvider first mints one.</summary>
    public string? AccessToken { get; set; }

    /// <summary>When AccessToken stops being usable. GoogleTokenProvider refreshes ahead of this rather than on a 401.</summary>
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }

    public required DateTimeOffset ConnectedAt { get; set; }

    /// <summary>When the event sync job last completed against this account, successful or not.</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>The last sync or refresh failure, surfaced on the admin Calendars page. Null once a sync succeeds.</summary>
    public string? LastSyncError { get; set; }

    /// <summary>
    /// Set when Google rejects the refresh token (invalid_grant), which is what
    /// an expired or revoked grant looks like. The account deliberately stays in
    /// the list so the admin sees "Reconnect" rather than a row that vanished.
    /// </summary>
    public bool NeedsReauth { get; set; }

    /// <summary>When false, the account is left connected but skipped by the sync job.</summary>
    public bool Enabled { get; set; } = true;

    public List<EfCalendar> Calendars { get; set; } = [];
}

/// <summary>
/// One calendar visible to a connected account, as returned by the provider's
/// calendar list. Every calendar an account can see gets a row; Included is
/// what decides whether it reaches the kiosk.
/// </summary>
[Table("Calendars")]
[Index(nameof(AccountId), nameof(ProviderCalendarId), IsUnique = true)]
public class EfCalendar
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }
    public EfCalendarAccount? Account { get; set; }

    /// <summary>The provider's own id for this calendar - for Google, the calendarList entry id (often an email address).</summary>
    public required string ProviderCalendarId { get; set; }

    public required string Name { get; set; }

    /// <summary>Hex color as the provider reports it. Kept so a calendar the admin hasn't recolored still looks like it does in Google.</summary>
    public string? ProviderColor { get; set; }

    /// <summary>Hex color the admin picked, winning over ProviderColor on the dashboard.</summary>
    public string? ColorOverride { get; set; }

    /// <summary>
    /// Defaults to false on purpose: connecting an account must not dump a work
    /// calendar onto a kitchen wall display. The admin opts each calendar in.
    /// </summary>
    public bool Included { get; set; }

    /// <summary>Ascending display order in the agenda.</summary>
    public int SortOrder { get; set; }

    /// <summary>The calendar's own IANA timezone as the provider reports it. Informational - event dates are resolved in the site timezone at sync time.</summary>
    public string? TimeZone { get; set; }

    /// <summary>True for the account's primary calendar, which is the one worth defaulting a first-time admin toward.</summary>
    public bool IsPrimary { get; set; }

    public List<EfCalendarEvent> Events { get; set; } = [];
}

/// <summary>
/// A cached event from one calendar. Written by the sync job, read by the
/// dashboard BFF - nothing in the request path talks to Google (docs/kiosk-architecture.md).
/// </summary>
[Table("CalendarEvents")]
[Index(nameof(CalendarId), nameof(ProviderEventId), IsUnique = true)]
public class EfCalendarEvent
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid CalendarId { get; set; }
    public EfCalendar? Calendar { get; set; }

    /// <summary>The provider's event id. For a recurring series this is the per-instance id, so each occurrence is its own row.</summary>
    public required string ProviderEventId { get; set; }

    public required string Title { get; set; }

    public string? Location { get; set; }

    public bool IsAllDay { get; set; }

    /// <summary>UTC instants. For an all-day event these are the day's bounds in the site timezone, so ordering against timed events still works.</summary>
    public required DateTimeOffset StartsAt { get; set; }
    public required DateTimeOffset EndsAt { get; set; }

    /// <summary>
    /// StartsAt/EndsAt resolved to calendar days in the site timezone at sync
    /// time, so "is this today or tomorrow" is an indexed date comparison
    /// instead of a timezone conversion the query has to redo per row.
    /// </summary>
    public DateOnly LocalStartDate { get; set; }
    public DateOnly LocalEndDate { get; set; }

    /// <summary>Provider status ("confirmed", "tentative", "cancelled"). Kept rather than filtered at sync time so a cancellation can be shown struck through if the design pass wants it.</summary>
    public string? Status { get; set; }

    public required DateTimeOffset FetchedAt { get; set; }
}

/// <summary>
/// One in-flight OAuth authorization, from the redirect to Google until the
/// callback comes back. A table rather than process memory because three API
/// replicas share one ingress: the callback can land on a different replica
/// than the one that started the flow.
/// </summary>
[Table("OAuthStates")]
[Index(nameof(State), IsUnique = true)]
public class EfOAuthState
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>The opaque anti-forgery value round-tripped through the provider. Single use - the callback deletes the row it matched.</summary>
    public required string State { get; set; }

    /// <summary>The PKCE verifier whose S256 challenge went out with the authorization request.</summary>
    public required string CodeVerifier { get; set; }

    /// <summary>The exact redirect URI sent to the provider, kept because the token exchange has to repeat it byte-for-byte.</summary>
    public required string RedirectUri { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>Short (minutes). Expired rows are swept opportunistically when a new flow starts, rather than by a job.</summary>
    public required DateTimeOffset ExpiresAt { get; set; }
}

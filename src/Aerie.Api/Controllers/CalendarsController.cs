using System.Text.RegularExpressions;
using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Models.Calendar;
using Aerie.Api.Services.Calendar;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Controllers;

/// <summary>
/// Read and manage what the family calendar draws from: which accounts are
/// connected, which of their calendars are included, and what color each one
/// gets. Following RoutinesController's DTO-and-write-request shape.
///
/// The connect flow itself lives next door in CalendarOAuthController, because
/// those two endpoints are navigated to rather than fetched.
///
/// Every route here sits behind the house wall (docs/auth-architecture.md),
/// which is a gate rather than permissions: any enrolled device can start the
/// connect flow or delete an account.
/// </summary>
[ApiController]
[Route("api/calendar")]
public partial class CalendarsController(
    AerieContext db,
    ICalendarDiscoveryService discovery,
    ICalendarSyncService sync,
    IGoogleOAuthService oauth,
    ILogger<CalendarsController> logger) : ControllerBase
{
    [HttpGet("accounts")]
    public async Task<IReadOnlyList<CalendarAccountDto>> GetAccounts(CancellationToken ct)
        => (await db.CalendarAccounts.AsNoTracking()
                .Include(a => a.Calendars)
                .OrderBy(a => a.AccountEmail)
                .ToListAsync(ct))
            .Select(ToDto)
            .ToList();

    /// <summary>
    /// Re-runs discovery against the provider. The manual counterpart to what
    /// the OAuth callback does automatically - for a calendar shared with the
    /// account after it was connected, or one whose name changed since.
    /// </summary>
    [HttpPost("accounts/{id:guid}/refresh-calendars")]
    public async Task<ActionResult<CalendarDiscoveryDto>> RefreshCalendars(Guid id, CancellationToken ct)
    {
        var result = await discovery.SyncCalendarListAsync(id, ct);
        if (result.Succeeded) return new CalendarDiscoveryDto(result.Added, result.Updated, result.Removed);

        if (result.Error == CalendarDiscoveryResult.UnknownAccount) return NotFound();

        // The account row carries the same message (LastSyncError), so a page
        // that just re-fetches instead of reading this body still shows it.
        return StatusCode(StatusCodes.Status502BadGateway, $"Could not list calendars from the provider: {result.Error}");
    }

    /// <summary>
    /// Runs the event sync now instead of at the next firing of
    /// SyncCalendarEvents, which is what makes "I just included this calendar"
    /// show up on the wall immediately rather than in up to five minutes.
    ///
    /// Always 200, even when every account failed: the service is fail-soft by
    /// contract, and the per-account reasons come back on the account rows.
    /// The counts are what tell the admin whether anything happened.
    /// </summary>
    [HttpPost("sync")]
    public async Task<CalendarSyncDto> Sync(CancellationToken ct)
    {
        var result = await sync.SyncAsync(ct);
        return new CalendarSyncDto(result.Accounts, result.Calendars, result.Written, result.Removed, result.FailedAccounts);
    }

    /// <summary>Sets the admin-owned half of a calendar. The provider-owned half (name, color, timezone) only ever changes through discovery.</summary>
    [HttpPut("calendars/{id:guid}")]
    public async Task<ActionResult<CalendarDto>> UpdateCalendar(Guid id, CalendarVisibilityRequest request, CancellationToken ct)
    {
        var colorOverride = NullIfEmpty(request.ColorOverride);
        if (colorOverride is not null && !HexColor().IsMatch(colorOverride))
            return BadRequest("colorOverride must be a hex color like #4285f4, or null to use the provider's color.");

        var calendar = await db.Calendars.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (calendar is null) return NotFound();

        calendar.Included = request.Included;
        calendar.ColorOverride = colorOverride;
        calendar.SortOrder = request.SortOrder;
        await db.SaveChangesAsync(ct);

        return ToDto(calendar);
    }

    /// <summary>
    /// Disconnects an account: tells the provider to forget the grant, then
    /// deletes the row, taking its calendars and their cached events by cascade.
    ///
    /// The revoke is best-effort on purpose. If Google is unreachable, the
    /// operator still asked for this account gone from Aerie, and leaving the
    /// row behind so a remote call can be retried would be answering a
    /// different question than the one they asked.
    /// </summary>
    [HttpDelete("accounts/{id:guid}")]
    public async Task<IActionResult> DeleteAccount(Guid id, CancellationToken ct)
    {
        var account = await db.CalendarAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null) return NotFound();

        if (SecretProtector.Unprotect(account.RefreshToken) is { } refreshToken)
        {
            if (!await oauth.RevokeAsync(refreshToken, ct))
                logger.LogWarning(
                    "Could not revoke the grant for calendar account {AccountId} with the provider; deleting it locally anyway", id);
        }
        else
        {
            // Already unusable, which is why NeedsReauth exists - there is
            // nothing left to revoke.
            logger.LogInformation("Calendar account {AccountId} had no readable refresh token to revoke", id);
        }

        db.CalendarAccounts.Remove(account);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Disconnected calendar account {AccountId}", id);
        return NoContent();
    }

    private static CalendarAccountDto ToDto(EfCalendarAccount account) => new(
        account.Id,
        account.Provider,
        account.AccountEmail,
        account.DisplayName,
        account.ConnectedAt,
        account.LastSyncedAt,
        account.LastSyncError,
        account.NeedsReauth,
        account.Enabled,
        account.Calendars
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .Select(ToDto)
            .ToList());

    private static CalendarDto ToDto(EfCalendar calendar) => new(
        calendar.Id,
        calendar.ProviderCalendarId,
        calendar.Name,
        calendar.ProviderColor,
        calendar.ColorOverride,
        calendar.Included,
        calendar.SortOrder,
        calendar.TimeZone,
        calendar.IsPrimary);

    /// <summary>
    /// This value is rendered into a style on the kiosk, so it is clamped to
    /// the one shape it is allowed to have rather than trusted to be a color
    /// because the admin page's picker usually sends one.
    /// </summary>
    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColor();

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

using System.Globalization;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// A point on an issue's calendar - its <see cref="EfHatchIssue.ReadyAt"/> or
/// its <see cref="EfHatchIssue.DueAt"/> - and whether anybody meant the time of
/// day when they set it.
///
/// The flag is the whole reason this type exists. "Renew the cert by the 1st"
/// and "renew it by the 1st at 5pm" are different promises, and an instant
/// alone cannot tell them apart: a bare date has to be pinned to some midnight
/// to be stored, and without a flag saying that midnight was invented, every
/// client that renders it in the wrong zone shows the day before.
/// </summary>
/// <remarks>
/// The rule that falls out of that, and which every client here follows: a
/// moment <em>with</em> a time is displayed in the reader's own zone, and a
/// moment <em>without</em> one is displayed in UTC, where the date components
/// are exactly the ones that were typed.
/// </remarks>
public readonly record struct IssueMoment(DateTimeOffset At, bool HasTime)
{
    /// <summary>A bare date - what an <c>&lt;input type="date"&gt;</c> submits.</summary>
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// An instant, to the second and always in UTC. Seconds rather than ticks
    /// because a due date is not a stopwatch, and the shorter string is the one
    /// a person types into <c>curl</c>.
    /// </summary>
    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>What a refusal says, so create and patch say it the same way.</summary>
    public const string Expected = "a date (2026-09-12) or an instant (2026-09-12T17:00:00Z)";

    /// <summary>
    /// The instant forms accepted, and deliberately only these. A general
    /// parse would take <c>09/12/2026</c> and read it in whatever order the
    /// invariant culture prefers, which silently turns one operator's 9
    /// December into another's 12 September. ISO 8601 has no such ambiguity, it
    /// is what every client here sends, and refusing the rest costs a person
    /// typing <c>curl</c> one correction rather than costing them a date they
    /// never notice is wrong.
    /// </summary>
    /// <remarks>
    /// <c>K</c> matches <c>Z</c>, a <c>+HH:mm</c> offset, or nothing at all -
    /// which is what lets the third form cover a browser's
    /// <c>toISOString()</c> and a hand-written <c>2026-09-12T17:00</c> alike.
    /// </remarks>
    private static readonly string[] InstantFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    /// <summary>
    /// Reads the wire form back. <c>yyyy-MM-dd</c> is a date and nothing else;
    /// one of <see cref="InstantFormats"/> is an instant, and one written
    /// without an offset is taken as UTC rather than as the server's local time
    /// - a server's timezone is an accident of deployment and must never change
    /// what a stored value means.
    /// </summary>
    public static bool TryParse(string? text, out IssueMoment moment)
    {
        moment = default;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;

        if (DateOnly.TryParseExact(trimmed, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            moment = new IssueMoment(new DateTimeOffset(date, TimeOnly.MinValue, TimeSpan.Zero), HasTime: false);
            return true;
        }

        if (DateTimeOffset.TryParseExact(
                trimmed,
                InstantFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var instant))
        {
            // Truncated on the way in rather than on the way out, so what a
            // later read returns is what the column holds. A browser submits
            // milliseconds it never asked anybody for.
            moment = new IssueMoment(
                new DateTimeOffset(instant.UtcDateTime.AddTicks(-(instant.UtcDateTime.Ticks % TimeSpan.TicksPerSecond)), TimeSpan.Zero),
                HasTime: true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The wire form: <c>2026-09-12</c>, or <c>2026-09-12T17:00:00Z</c>. Read
    /// and write use this one shape, so a value handed back to the API unchanged
    /// leaves the issue where it was - which is what makes a PATCH that means to
    /// touch only the title safe to send whole.
    /// </summary>
    public string Format() =>
        At.ToUniversalTime().ToString(HasTime ? InstantFormat : DateFormat, CultureInfo.InvariantCulture);

    /// <summary>The same, for a moment an issue may not have. Null stays null.</summary>
    public static string? Format(DateTimeOffset? at, bool hasTime) =>
        at is { } instant ? new IssueMoment(instant, hasTime).Format() : null;
}

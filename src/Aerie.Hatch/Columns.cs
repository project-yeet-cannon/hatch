namespace Aerie.Hatch;

/// <summary>
/// Finding a column by what it is called, and deciding whether a card's ready
/// date has arrived.
/// </summary>
/// <remarks>
/// Statuses are rows and the operator may rename them, so a column is found by
/// a loose match on its name - case, spaces and punctuation all ignored - and
/// never by a hardcoded id. That is the rule <c>hatch.sh</c> carried in a jq
/// filter, and it is why <c>todo</c> reaches the column the board calls
/// <c>To Do</c>.
/// </remarks>
public static class Columns
{
    /// <summary>The letters and the digits, lowercased. Everything else is punctuation somebody typed.</summary>
    public static string Normalise(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        var written = 0;

        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c)) buffer[written++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..written]);
    }

    /// <summary>The column whose name matches, or null where none does.</summary>
    public static StatusDto? Find(IEnumerable<StatusDto> statuses, string want)
    {
        var wanted = Normalise(want);
        return statuses.FirstOrDefault(s => Normalise(s.Name) == wanted);
    }

    /// <summary>What there is instead, for the sentence that says there is no such column.</summary>
    public static string Named(IEnumerable<StatusDto> statuses) => string.Join(", ", statuses.Select(s => s.Name));

    /// <summary>
    /// The calendar day a timestamp falls on, in the reader's zone.
    /// </summary>
    /// <remarks>
    /// "Ready" is a question about calendar days and not about elapsed hours: an
    /// issue is workable from the start of the day it names, whatever hour it
    /// was set to. A date with no time in it is already a calendar day and is
    /// read as one; a full timestamp is folded into the reader's zone first.
    /// See <c>schedule.ts</c>, which holds the same rule for the board.
    /// </remarks>
    public static long DayOf(string when, TimeSpan offset)
    {
        var seconds = when.Length == 10 && DateOnly.TryParse(when, out var date)
            ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds()
            : DateTimeOffset.Parse(when).ToUnixTimeSeconds() + (long)offset.TotalSeconds;

        return Floor(seconds);
    }

    /// <summary>Today, as the same day number.</summary>
    public static long Today(DateTimeOffset now) => Floor(now.ToUnixTimeSeconds() + (long)now.Offset.TotalSeconds);

    /// <summary>Whether this card's ready date has arrived. A card with none is always ready.</summary>
    public static bool Ready(string? readyAt, DateTimeOffset now) =>
        readyAt is not { Length: > 0 } when || DayOf(when, now.Offset) <= Today(now);

    /// <summary>Days since the epoch, rounding towards the past on either side of it.</summary>
    private static long Floor(long seconds) =>
        seconds >= 0 ? seconds / 86400 : (seconds - 86399) / 86400;
}

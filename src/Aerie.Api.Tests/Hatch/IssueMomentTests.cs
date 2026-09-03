using Aerie.Api.Modules.Hatch;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// The two dates an issue carries are written down as one string each, and this
/// is the type that decides what that string means. The property worth
/// protecting: whatever comes out of <see cref="IssueMoment.Format"/> goes back
/// into <see cref="IssueMoment.TryParse"/> as the same moment - a client that
/// PATCHes an issue back unchanged must not move its dates.
/// </summary>
public class IssueMomentTests
{
    [Theory]
    [InlineData("2026-09-12")]
    [InlineData("2026-09-12T17:00:00Z")]
    [InlineData("2026-01-01")]
    public void WhatIsFormatted_ParsesBackTheSame(string text)
    {
        Assert.True(IssueMoment.TryParse(text, out var moment));

        Assert.Equal(text, moment.Format());
    }

    /// <summary>
    /// A bare date is a date, and stays one. Storing it needs some midnight, but
    /// the flag is what stops a reader in another zone from drawing the 11th.
    /// </summary>
    [Fact]
    public void ABareDate_CarriesNoTimeOfDay()
    {
        Assert.True(IssueMoment.TryParse("2026-09-12", out var moment));

        Assert.False(moment.HasTime);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero), moment.At);
    }

    [Fact]
    public void AnInstant_CarriesItsTimeOfDay()
    {
        Assert.True(IssueMoment.TryParse("2026-09-12T17:00:00Z", out var moment));

        Assert.True(moment.HasTime);
    }

    /// <summary>
    /// An offset is honoured and then normalised away. 5pm in one zone and the
    /// same instant written in another are one moment, and they have to format
    /// identically or a re-sent value reads as an edit.
    /// </summary>
    [Fact]
    public void AnOffset_IsResolvedToUtc()
    {
        Assert.True(IssueMoment.TryParse("2026-09-12T17:00:00-04:00", out var moment));

        Assert.Equal("2026-09-12T21:00:00Z", moment.Format());
    }

    /// <summary>
    /// A server's timezone is an accident of where it was deployed. It must
    /// never be the thing that decides what a stored date means.
    /// </summary>
    [Fact]
    public void AnInstantWithNoOffset_IsReadAsUtcRatherThanAsTheServersClock()
    {
        Assert.True(IssueMoment.TryParse("2026-09-12T17:00:00", out var moment));

        Assert.Equal("2026-09-12T17:00:00Z", moment.Format());
    }

    /// <summary>A browser submits milliseconds nobody asked for; they do not survive.</summary>
    [Fact]
    public void Milliseconds_AreDropped()
    {
        Assert.True(IssueMoment.TryParse("2026-09-12T17:00:00.482Z", out var moment));

        Assert.Equal("2026-09-12T17:00:00Z", moment.Format());
    }

    /// <summary>A browser's <c>toISOString()</c>, and a time typed without seconds.</summary>
    [Theory]
    [InlineData("2026-09-12T17:00:00.000Z", "2026-09-12T17:00:00Z")]
    [InlineData("2026-09-12T17:00Z", "2026-09-12T17:00:00Z")]
    public void TheFormsAClientSends_AreAccepted(string sent, string stored)
    {
        Assert.True(IssueMoment.TryParse(sent, out var moment));

        Assert.Equal(stored, moment.Format());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tomorrow")]
    [InlineData("next friday")]
    [InlineData("2026-13-45")]
    public void WhatIsNotADate_IsRefused(string text)
    {
        Assert.False(IssueMoment.TryParse(text, out _));
    }

    /// <summary>
    /// The one refusal that is a judgement rather than a parse failure.
    /// <c>09/12/2026</c> is a perfectly good date to most parsers and means
    /// September to some readers and December to others; taking it would hand
    /// somebody a date three months wrong that nothing on screen would ever
    /// flag.
    /// </summary>
    [Theory]
    [InlineData("09/12/2026")]
    [InlineData("12 September 2026")]
    [InlineData("Sep 12, 2026")]
    public void ADateWrittenAmbiguously_IsRefusedRatherThanGuessedAt(string text)
    {
        Assert.False(IssueMoment.TryParse(text, out _));
    }
}

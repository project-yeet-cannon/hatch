using Aerie.Api.Common;
using Aerie.Api.Modules.Quill;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Tests.Quill;

/// <summary>
/// The one thing QuillContext does that no other module context does: note text
/// is protected at rest by a value converter rather than by a call at each write
/// path.
///
/// These tests are the reason that choice is safe. A module that protects in
/// its controller fails silently when a new write path forgets - SecretProtector
/// reads an untagged legacy value as v1, so the unprotected row round-trips
/// perfectly and nobody finds out. Here the guarantee is a property of the
/// model, and the model can be asserted on.
/// </summary>
public class QuillContextTests
{
    /// <summary>
    /// The regression that matters, written to catch a column that does not
    /// exist yet: every string a person types into a note is protected, and
    /// adding an unprotected one fails here rather than in a database dump.
    /// </summary>
    [Fact]
    public void EveryStringOnANote_IsProtectedAtRest()
    {
        using var db = NewContext();
        var note = db.Model.FindEntityType(typeof(QuillNote))!;

        var unprotected = note.GetProperties()
            .Where(p => p.ClrType == typeof(string) && p.GetValueConverter() is null)
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(unprotected);
    }

    [Theory]
    [InlineData(nameof(QuillNote.Title))]
    [InlineData(nameof(QuillNote.Body))]
    public void Protection_RoundTrips_AndTheStoredFormIsNotWhatWasTyped(string property)
    {
        const string plaintext = "the wifi password is hunter2";
        var converter = ConverterFor(property);

        var stored = (string)converter.ConvertToProvider(plaintext)!;

        Assert.DoesNotContain("hunter2", stored, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith($"{SecretProtector.CurrentScheme}:", stored);
        Assert.Equal(plaintext, converter.ConvertFromProvider(stored));
    }

    /// <summary>
    /// Blank stays blank and untagged - an untitled note is the ordinary case,
    /// and tagging "" would turn every absent title into a present one that
    /// decodes to nothing. SecretProtector already decides this; the converter
    /// must not undo it.
    /// </summary>
    [Fact]
    public void AnEmptyTitle_StaysEmptyThroughTheConverter()
    {
        var converter = ConverterFor(nameof(QuillNote.Title));

        Assert.Equal("", converter.ConvertToProvider(""));
        Assert.Equal("", converter.ConvertFromProvider(""));
    }

    /// <summary>
    /// A row hand-edited in the database, or written by a scheme a rolled-back
    /// build does not know, reads as empty rather than throwing. One damaged
    /// note should cost that note its text, not throw out of the list that draws
    /// every other note.
    /// </summary>
    [Fact]
    public void AnUnreadableStoredValue_ReadsAsEmptyRatherThanThrowing()
    {
        var converter = ConverterFor(nameof(QuillNote.Body));

        Assert.Equal("", converter.ConvertFromProvider("v99:from the future"));
        Assert.Equal("", converter.ConvertFromProvider("v1:not base64 at all!!"));
    }

    /// <summary>
    /// The index the module's only query needs. Both columns and in this order:
    /// PersonId alone would answer the filter and leave a sort in front of every
    /// read of the list screen.
    /// </summary>
    [Fact]
    public void Notes_AreIndexedByPersonAndRecency()
    {
        using var db = NewContext();

        var index = db.Model.FindEntityType(typeof(QuillNote))!.GetIndexes().Single();

        Assert.Equal(
            [nameof(QuillNote.PersonId), nameof(QuillNote.UpdatedAt)],
            index.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Notes_LiveInTheirOwnSchema()
    {
        using var db = NewContext();

        Assert.Equal(QuillContext.Schema, db.Model.FindEntityType(typeof(QuillNote))!.GetSchema());
    }

    private static Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter ConverterFor(string property)
    {
        using var db = NewContext();
        return db.Model.FindEntityType(typeof(QuillNote))!.FindProperty(property)!.GetValueConverter()!;
    }

    private static QuillContext NewContext() =>
        new(new DbContextOptionsBuilder<QuillContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}

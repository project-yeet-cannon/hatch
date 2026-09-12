using Hatch.Api.Common;

namespace Hatch.Api.Tests.People;

/// <summary>
/// What may be stored as a person's photo, decided from the bytes alone.
///
/// The interesting cases are all the same shape: something that is not an image
/// arriving where an image is expected, and the question is whether it is
/// recognized as not-an-image *before* it is stored and later served back from
/// the install's own origin.
/// </summary>
public class PersonPhotoTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
    private static readonly byte[] Gif = "GIF89a...."u8.ToArray();
    private static readonly byte[] Webp = [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, .. "WEBPVP8 "u8];

    [Fact]
    public void RecognizesTheFourFormatsABrowserCanProduce()
    {
        Assert.Equal("image/png", Detect(Png));
        Assert.Equal("image/jpeg", Detect(Jpeg));
        Assert.Equal("image/gif", Detect(Gif));
        Assert.Equal("image/webp", Detect(Webp));
    }

    [Fact]
    public void RefusesAnSvgEvenThoughItIsAnImage()
    {
        // The one exclusion worth a test of its own. An SVG is a document that
        // can carry script, so serving one back from this origin would be a
        // stored XSS wearing an avatar - which is why "is it an image" is not
        // the question this class answers.
        Assert.False(PersonPhoto.TryDetectContentType("<svg xmlns=\"http://www.w3.org/2000/svg\"><script/></svg>"u8, out _, out var error));
        Assert.Equal(PersonPhoto.UnsupportedError, error);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><script>alert(1)</script>")]
    [InlineData("#!/bin/sh\nrm -rf /")]
    [InlineData("just some text")]
    public void RefusesAThingThatIsNotAnImageAtAll(string content)
    {
        Assert.False(PersonPhoto.TryDetectContentType(System.Text.Encoding.UTF8.GetBytes(content), out _, out var error));
        Assert.Equal(PersonPhoto.UnsupportedError, error);
    }

    [Fact]
    public void RefusesARiffContainerThatIsNotAWebp()
    {
        // "RIFF" alone is also WAV and AVI. Checking only the first four bytes
        // is the plausible-looking mistake this asserts against.
        byte[] wav = [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, .. "WAVEfmt "u8];

        Assert.False(PersonPhoto.TryDetectContentType(wav, out _, out var error));
        Assert.Equal(PersonPhoto.UnsupportedError, error);
    }

    [Fact]
    public void RefusesTwoBytesThatHappenToLookLikeAJpeg()
    {
        // A JPEG's third byte is always 0xFF - the first segment's marker. A
        // two-byte check would accept this.
        Assert.False(PersonPhoto.TryDetectContentType([0xFF, 0xD8, 0x00], out _, out var error));
        Assert.Equal(PersonPhoto.UnsupportedError, error);
    }

    [Fact]
    public void RefusesAnUploadOverTheCapBeforeLookingAtWhatItIs()
    {
        // Valid PNG bytes, too many of them. The order matters: a size check
        // that ran second would have already accepted the header of something
        // far too big to keep.
        var huge = new byte[PersonPhoto.MaxBytes + 1];
        Png.CopyTo(huge, 0);

        Assert.False(PersonPhoto.TryDetectContentType(huge, out _, out var error));
        Assert.Equal(PersonPhoto.TooLargeError, error);
    }

    [Fact]
    public void AcceptsAnUploadExactlyAtTheCap()
    {
        var atLimit = new byte[PersonPhoto.MaxBytes];
        Png.CopyTo(atLimit, 0);

        Assert.Equal("image/png", Detect(atLimit));
    }

    [Fact]
    public void RefusesAnEmptyBodyWithItsOwnSentence()
    {
        // Distinguished from "not an image" because it is a different mistake:
        // nothing was picked, rather than the wrong thing was.
        Assert.False(PersonPhoto.TryDetectContentType([], out _, out var error));
        Assert.Equal(PersonPhoto.EmptyError, error);
    }

    [Fact]
    public void DoesNotReadPastTheEndOfAShortUpload()
    {
        // Four bytes that are the start of a WebP signature and nothing else.
        // The bounds check is the whole test - the failure mode it guards is an
        // exception rather than a refusal.
        Assert.False(PersonPhoto.TryDetectContentType("RIFF"u8, out _, out var error));
        Assert.Equal(PersonPhoto.UnsupportedError, error);
    }

    private static string Detect(ReadOnlySpan<byte> bytes)
    {
        Assert.True(PersonPhoto.TryDetectContentType(bytes, out var contentType, out _));
        return contentType;
    }
}

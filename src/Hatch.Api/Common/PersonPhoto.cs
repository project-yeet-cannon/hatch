namespace Hatch.Api.Common;

/// <summary>
/// Decides whether a blob someone uploaded is an image, and which kind.
///
/// The answer comes from the bytes and only from the bytes. A request's
/// Content-Type is a claim by the uploader, and the whole point of validating
/// an upload is that the uploader may be lying; storing a declared type and
/// serving it back is how a text/html "photo" ends up being rendered as a page
/// on the install's own origin. What this returns is what gets stored, and what
/// gets stored is what gets served.
///
/// Four formats, because those are the four every browser can both produce and
/// display. SVG is deliberately absent and is the one exclusion worth stating:
/// it is a document, it can carry script, and "an image format that executes"
/// is exactly the thing serving user uploads from your own origin must not do.
///
/// There is no re-encoding step, and that is a decision rather than an
/// omission. Re-encoding would mean an imaging library - the obvious one is
/// split-licensed in a way a repo headed for open-source release should not
/// inherit (docs/ethos.md) - to solve a problem this design does not have: the
/// bytes are never interpreted by the server, they are sized-capped, and they
/// are served back with a sniffed type and <c>Content-Disposition: attachment</c>
/// semantics via <c>X-Content-Type-Options: nosniff</c>. The admin app downscales
/// before it uploads, so the cap is generous rather than tight.
/// </summary>
public static class PersonPhoto
{
    /// <summary>
    /// The hard ceiling on one stored photo. Generous for an avatar the client
    /// has already downscaled, and small enough that a person's row is never
    /// the reason a backup got big.
    /// </summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    public const string TooLargeError = "That image is larger than 2 MB. Try a smaller one.";
    public const string UnsupportedError = "That doesn't look like a PNG, JPEG, GIF, or WebP image.";
    public const string EmptyError = "No image was uploaded.";

    /// <summary>
    /// The sniffed content type, or null with a reason. Null is always a 400 -
    /// there is no case where an unrecognized upload should be stored and
    /// sorted out later.
    /// </summary>
    public static bool TryDetectContentType(ReadOnlySpan<byte> bytes, out string contentType, out string error)
    {
        contentType = string.Empty;
        error = string.Empty;

        if (bytes.Length == 0)
        {
            error = EmptyError;
            return false;
        }

        if (bytes.Length > MaxBytes)
        {
            error = TooLargeError;
            return false;
        }

        // PNG: the 8-byte signature, whose \r\n and ^Z bytes exist precisely so
        // that a transfer which mangled the file is detectable here.
        if (StartsWith(bytes, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            contentType = "image/png";
            return true;
        }

        // JPEG: SOI marker. The third byte is the first segment's marker and is
        // always 0xFF, which is what separates a JPEG from the two-byte prefix
        // appearing by chance.
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            contentType = "image/jpeg";
            return true;
        }

        // GIF: both versions are still in the wild, and an animated one is a
        // perfectly good thing for a household to put on a person.
        if (StartsWith(bytes, "GIF87a"u8) || StartsWith(bytes, "GIF89a"u8))
        {
            contentType = "image/gif";
            return true;
        }

        // WebP: a RIFF container whose form type is WEBP. Both halves are
        // required - "RIFF" alone is also WAV and AVI.
        if (bytes.Length >= 12 && StartsWith(bytes, "RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            contentType = "image/webp";
            return true;
        }

        error = UnsupportedError;
        return false;
    }

    private static bool StartsWith(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> prefix) =>
        bytes.Length >= prefix.Length && bytes[..prefix.Length].SequenceEqual(prefix);
}

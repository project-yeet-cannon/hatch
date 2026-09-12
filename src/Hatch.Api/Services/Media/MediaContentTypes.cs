using Microsoft.AspNetCore.StaticFiles;

namespace Hatch.Api.Services.Media;

/// <summary>
/// Content types for the audio formats a music library actually holds.
/// StaticFileMiddleware 404s any extension it can't type (rather than guessing
/// or streaming it as octet-stream), and the built-in extension map misses
/// several common lossless/streaming formats - so .flac and .m4a files would
/// silently not play without this.
/// </summary>
public static class MediaContentTypes
{
    /// <summary>The media_content_type handed to HA's play_media when a caller doesn't pick one - every file this library holds is music. Shared by DevicesController.PlayMedia and RoutineActionExecutor.</summary>
    public const string DefaultPlayMediaType = "music";

    private static readonly (string Extension, string ContentType)[] AudioTypes =
    [
        (".mp3", "audio/mpeg"),
        (".flac", "audio/flac"),
        (".m4a", "audio/mp4"),
        (".alac", "audio/mp4"),
        (".aac", "audio/aac"),
        (".opus", "audio/opus"),
        (".ogg", "audio/ogg"),
        (".oga", "audio/ogg"),
        (".wav", "audio/wav"),
        (".wma", "audio/x-ms-wma"),
        (".aif", "audio/aiff"),
        (".aiff", "audio/aiff"),
    ];

    /// <summary>The default extension map plus <see cref="AudioTypes"/>, for the media library's StaticFileOptions.</summary>
    public static FileExtensionContentTypeProvider CreateProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        foreach (var (extension, contentType) in AudioTypes)
            provider.Mappings[extension] = contentType;
        return provider;
    }
}

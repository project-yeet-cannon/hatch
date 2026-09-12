using Hatch.Api.Modules.Photos;

namespace Hatch.Api.Tests.Photos;

/// <summary>
/// An Immich that answers from a script and counts what it was asked, so the
/// tests around it are about Hatch's behaviour rather than about HTTP -
/// ImmichClientTests already covers the wire.
/// </summary>
internal sealed class StubImmichClient : IImmichClient
{
    public ImmichListResult<ImmichAlbum> Albums { get; set; } = ImmichListResult<ImmichAlbum>.Ok([]);

    /// <summary>Per-album photo lists. An album with no entry answers "not found", which is what Immich does for one deleted since the last refresh.</summary>
    public Dictionary<string, ImmichListResult<ImmichAsset>> Photos { get; } = [];

    public ImmichResult<ImmichServer> Server { get; set; } = ImmichResult<ImmichServer>.Ok(new ImmichServer("v3.0.0"));

    public ImmichResult<ImmichImage> Image { get; set; } =
        ImmichResult<ImmichImage>.Ok(new ImmichImage([1, 2, 3], "image/jpeg"));

    public List<string> AlbumPhotoCalls { get; } = [];

    public List<(string AssetId, string Size)> ImageCalls { get; } = [];

    public int AlbumListCalls { get; private set; }

    public Task<ImmichResult<ImmichServer>> GetServerAsync(CancellationToken ct) => Task.FromResult(Server);

    public Task<ImmichListResult<ImmichAlbum>> ListAlbumsAsync(CancellationToken ct)
    {
        AlbumListCalls++;
        return Task.FromResult(Albums);
    }

    public Task<ImmichListResult<ImmichAsset>> ListAlbumPhotosAsync(string albumId, CancellationToken ct)
    {
        AlbumPhotoCalls.Add(albumId);
        return Task.FromResult(Photos.TryGetValue(albumId, out var photos)
            ? photos
            : ImmichListResult<ImmichAsset>.Failed(ImmichClient.NotFound));
    }

    public Task<ImmichResult<ImmichImage>> GetImageAsync(string assetId, string size, CancellationToken ct)
    {
        ImageCalls.Add((assetId, size));
        return Task.FromResult(Image);
    }
}

/// <summary>A library that serves what a test put in it, and remembers being invalidated - which is the assertion for "the wall sees this now, not in fifteen minutes".</summary>
internal sealed class StubPhotoLibrary(params LibraryPhoto[] photos) : IPhotoLibrary
{
    public int Invalidations { get; private set; }

    public string? Error { get; set; }

    public List<LibraryPhoto> Photos { get; } = [.. photos];

    public Task<PhotoLibrarySnapshot> GetAsync(CancellationToken ct) => Task.FromResult(new PhotoLibrarySnapshot(
        Photos,
        Photos.Select(p => p.AssetId).ToHashSet(StringComparer.Ordinal),
        DateTimeOffset.UnixEpoch,
        Error));

    public void Invalidate() => Invalidations++;
}

using Aerie.Api.Modules.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Photos;

/// <summary>
/// Covers the ownership split the service exists to enforce - Immich owns what
/// an album is, the operator owns what the wall does with it - and the failure
/// that would otherwise destroy the operator's half: a fetch that failed being
/// read as "there are no albums any more".
/// </summary>
public class PhotoAlbumServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AddsEveryAlbum_ExcludedUntilSomeoneChoosesIt()
    {
        var h = NewHarness(returning: [Album("a1", "Christmas 2025", 42), Album("a2", "Hikes", 7)]);

        var result = await h.Service.SyncAlbumsAsync(default);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Added);
        var albums = await Albums(h.Db);
        Assert.Equal(["a1", "a2"], albums.Select(a => a.ImmichAlbumId));
        // The whole reason Included defaults to false: connecting a library
        // must not put every photo in it on the kitchen wall.
        Assert.All(albums, a => Assert.False(a.Included));
        Assert.Equal([0, 1], albums.Select(a => a.SortOrder));
        Assert.Equal(42, albums[0].AssetCount);
    }

    [Fact]
    public async Task ARefreshKeepsTheChoices_AndRefreshesImmichsOwnFields()
    {
        var h = NewHarness(
            seed: [Existing("a1", "Old name", included: true, sortOrder: 7)],
            returning: [Album("a1", "Christmas 2025", 42, cover: "cover-1")]);

        var result = await h.Service.SyncAlbumsAsync(default);

        Assert.Equal(1, result.Updated);
        var album = (await Albums(h.Db)).Single();
        Assert.Equal("Christmas 2025", album.Name);
        Assert.Equal(42, album.AssetCount);
        Assert.Equal("cover-1", album.ThumbnailAssetId);
        // Untouched, and this is the point: "Refresh albums" must not quietly
        // take a wedding album off the wall.
        Assert.True(album.Included);
        Assert.Equal(7, album.SortOrder);
    }

    [Fact]
    public async Task AnAlbumThatIsGoneUpstreamGoesHereToo()
    {
        var h = NewHarness(
            seed: [Existing("a1", "Christmas 2025"), Existing("a2", "Deleted in Immich")],
            returning: [Album("a1", "Christmas 2025", 42)]);

        var result = await h.Service.SyncAlbumsAsync(default);

        Assert.Equal(1, result.Removed);
        Assert.Equal(["a1"], (await Albums(h.Db)).Select(a => a.ImmichAlbumId));
    }

    /// <summary>
    /// The one that would hurt. An unreachable Immich returns an empty list,
    /// and treating that as the truth would wipe every album - and every
    /// Included flag on them - the first time the photo server restarted.
    /// </summary>
    [Fact]
    public async Task AFailedFetchDeletesNothing()
    {
        var h = NewHarness(seed: [Existing("a1", "Christmas 2025", included: true)], failWith: ImmichClient.Unreachable);

        var result = await h.Service.SyncAlbumsAsync(default);

        Assert.False(result.Succeeded);
        Assert.Equal(ImmichClient.Unreachable, result.Error);
        var album = (await Albums(h.Db)).Single();
        Assert.True(album.Included);
        Assert.Equal(0, h.Library.Invalidations);
    }

    /// <summary>An unchanged album is not an updated one - otherwise every refresh reports the whole library as churn and the count says nothing.</summary>
    [Fact]
    public async Task ARefreshThatChangesNothingCountsNothing_AndLeavesTheCacheAlone()
    {
        var h = NewHarness(
            seed: [Existing("a1", "Christmas 2025", assetCount: 42, cover: "cover-1")],
            returning: [Album("a1", "Christmas 2025", 42, cover: "cover-1")]);

        var result = await h.Service.SyncAlbumsAsync(default);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Equal(0, h.Library.Invalidations);
    }

    /// <summary>A rename is a caption change on the wall, so the cached library has to be rebuilt rather than left saying the old name for a quarter of an hour.</summary>
    [Fact]
    public async Task ARenameDropsTheCachedLibrary()
    {
        var h = NewHarness(
            seed: [Existing("a1", "Old name", included: true)],
            returning: [Album("a1", "Christmas 2025", 42)]);

        await h.Service.SyncAlbumsAsync(default);

        Assert.Equal(1, h.Library.Invalidations);
    }

    [Fact]
    public async Task ANewAlbumLandsAfterTheOnesAlreadyArranged()
    {
        var h = NewHarness(
            seed: [Existing("a1", "Christmas 2025", sortOrder: 4)],
            returning: [Album("a1", "Christmas 2025", 42), Album("a2", "Hikes", 7)]);

        await h.Service.SyncAlbumsAsync(default);

        var albums = await Albums(h.Db);
        Assert.Equal(5, albums.Single(a => a.ImmichAlbumId == "a2").SortOrder);
    }

    private static ImmichAlbum Album(string id, string name, int count, string? cover = null) =>
        new(id, name, null, count, cover, Now.AddDays(-1));

    private static PhotoAlbum Existing(
        string id, string name, bool included = false, int sortOrder = 0, int assetCount = 0, string? cover = null) =>
        new()
        {
            ImmichAlbumId = id,
            Name = name,
            Included = included,
            SortOrder = sortOrder,
            AssetCount = assetCount,
            ThumbnailAssetId = cover,
            ProviderUpdatedAt = Now.AddDays(-1),
            CreatedAt = Now.AddDays(-2),
            UpdatedAt = Now.AddDays(-2),
        };

    private static Harness NewHarness(
        IReadOnlyList<PhotoAlbum>? seed = null,
        IReadOnlyList<ImmichAlbum>? returning = null,
        string? failWith = null)
    {
        var options = new DbContextOptionsBuilder<PhotosContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using (var seedDb = new PhotosContext(options))
        {
            seedDb.Albums.AddRange(seed ?? []);
            seedDb.SaveChanges();
        }

        var immich = new StubImmichClient
        {
            Albums = failWith is null
                ? ImmichListResult<ImmichAlbum>.Ok(returning ?? [])
                : ImmichListResult<ImmichAlbum>.Failed(failWith),
        };

        var db = new PhotosContext(options);
        var library = new StubPhotoLibrary();
        var service = new PhotoAlbumService(
            db, immich, library, new FakeTimeProvider(Now), NullLogger<PhotoAlbumService>.Instance);

        return new Harness(service, db, immich, library);
    }

    /// <summary>Read back untracked and in the order the admin page draws them, so an assertion sees what was saved rather than what the service still holds.</summary>
    private static async Task<List<PhotoAlbum>> Albums(PhotosContext db) =>
        await db.Albums.AsNoTracking().OrderBy(a => a.SortOrder).ThenBy(a => a.Name).ToListAsync();

    private sealed record Harness(
        PhotoAlbumService Service, PhotosContext Db, StubImmichClient Immich, StubPhotoLibrary Library);
}

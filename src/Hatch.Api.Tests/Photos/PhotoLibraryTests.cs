using Hatch.Api.Modules.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Hatch.Api.Tests.Photos;

/// <summary>
/// Covers the cache's two jobs: not asking Immich for the same library over and
/// over, and not going dark when Immich is the thing that is broken. Plus the
/// one that is a security property rather than a performance one - the asset
/// allow-list only ever holds photos from included albums.
/// </summary>
public class PhotoLibraryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadsOnlyTheIncludedAlbums_AndCaptionsWithTheirNames()
    {
        var h = NewHarness(
            albums: [Included("a1", "Christmas 2025"), Excluded("a2", "Work receipts")],
            photos: new() { ["a1"] = [Photo("p1"), Photo("p2")], ["a2"] = [Photo("p3")] });

        var snapshot = await h.Library.GetAsync(default);

        Assert.Equal(["p1", "p2"], snapshot.Photos.Select(p => p.AssetId));
        Assert.All(snapshot.Photos, p => Assert.Equal("Christmas 2025", p.AlbumName));
        Assert.Equal(["a1"], h.Immich.AlbumPhotoCalls);
    }

    /// <summary>
    /// The allow-list is what makes the image endpoint a proxy for the wall
    /// rather than a hole through to every photo in the house. A photo in an
    /// album nobody included must not be in it.
    /// </summary>
    [Fact]
    public async Task TheAllowListHoldsExactlyTheIncludedPhotos()
    {
        var h = NewHarness(
            albums: [Included("a1", "Christmas 2025"), Excluded("a2", "Work receipts")],
            photos: new() { ["a1"] = [Photo("p1")], ["a2"] = [Photo("p3")] });

        var snapshot = await h.Library.GetAsync(default);

        Assert.Contains("p1", snapshot.AssetIds);
        Assert.DoesNotContain("p3", snapshot.AssetIds);
    }

    [Fact]
    public async Task ASecondReadInsideTheTtlDoesNotAskImmichAgain()
    {
        var h = NewHarness(albums: [Included("a1", "Christmas 2025")], photos: new() { ["a1"] = [Photo("p1")] });

        await h.Library.GetAsync(default);
        h.Time.Advance(PhotoLibrary.CacheTtl - TimeSpan.FromMinutes(1));
        await h.Library.GetAsync(default);

        Assert.Single(h.Immich.AlbumPhotoCalls);
    }

    [Fact]
    public async Task ARebuildFollowsTheTtl()
    {
        var h = NewHarness(albums: [Included("a1", "Christmas 2025")], photos: new() { ["a1"] = [Photo("p1")] });

        await h.Library.GetAsync(default);
        h.Time.Advance(PhotoLibrary.CacheTtl + TimeSpan.FromMinutes(1));
        await h.Library.GetAsync(default);

        Assert.Equal(2, h.Immich.AlbumPhotoCalls.Count);
    }

    /// <summary>What makes a toggle on the admin page show up on the wall on its next poll instead of at the next expiry.</summary>
    [Fact]
    public async Task InvalidateForcesTheNextReadToRefetch()
    {
        var h = NewHarness(albums: [Included("a1", "Christmas 2025")], photos: new() { ["a1"] = [Photo("p1")] });

        await h.Library.GetAsync(default);
        h.Library.Invalidate();
        await h.Library.GetAsync(default);

        Assert.Equal(2, h.Immich.AlbumPhotoCalls.Count);
    }

    /// <summary>
    /// The other replica's toggle. Invalidate only reaches one process, so the
    /// selection itself is re-read on a short interval and a change to it
    /// rebuilds immediately rather than waiting out the fifteen minutes.
    /// </summary>
    [Fact]
    public async Task AnAlbumIncludedElsewhereIsPickedUpWithoutWaitingOutTheTtl()
    {
        var h = NewHarness(
            albums: [Included("a1", "Christmas 2025"), Excluded("a2", "Hikes")],
            photos: new() { ["a1"] = [Photo("p1")], ["a2"] = [Photo("p3")] });

        await h.Library.GetAsync(default);

        await using (var db = h.NewDb())
        {
            var album = await db.Albums.SingleAsync(a => a.ImmichAlbumId == "a2");
            album.Included = true;
            await db.SaveChangesAsync();
        }

        h.Time.Advance(PhotoLibrary.SelectionRecheckInterval + TimeSpan.FromSeconds(1));
        var snapshot = await h.Library.GetAsync(default);

        Assert.Equal(["p1", "p3"], snapshot.Photos.Select(p => p.AssetId));
    }

    /// <summary>
    /// A photo frame showing a fifteen-minute-old set of photos is not a bug. A
    /// photo frame going black because Immich restarted is.
    /// </summary>
    [Fact]
    public async Task AFailedRebuildKeepsTheLastGoodPhotos_AndSaysWhy()
    {
        var h = NewHarness(albums: [Included("a1", "Christmas 2025")], photos: new() { ["a1"] = [Photo("p1")] });
        await h.Library.GetAsync(default);

        h.Immich.Photos["a1"] = ImmichListResult<ImmichAsset>.Failed(ImmichClient.Unreachable);
        h.Time.Advance(PhotoLibrary.CacheTtl + TimeSpan.FromMinutes(1));
        var snapshot = await h.Library.GetAsync(default);

        Assert.Equal(["p1"], snapshot.Photos.Select(p => p.AssetId));
        Assert.Equal(ImmichClient.Unreachable, snapshot.Error);
    }

    [Fact]
    public async Task AFailedRebuildRetriesSoonerThanTheTtl()
    {
        var h = NewHarness(albums: [Included("a1", "Christmas 2025")]);

        await h.Library.GetAsync(default);
        h.Time.Advance(PhotoLibrary.FailureRetryAfter + TimeSpan.FromSeconds(1));
        await h.Library.GetAsync(default);

        Assert.Equal(2, h.Immich.AlbumPhotoCalls.Count);
    }

    /// <summary>One album deleted upstream must not take the other three off the wall with it.</summary>
    [Fact]
    public async Task OneAlbumFailingLeavesTheRestOnTheWall()
    {
        var h = NewHarness(
            albums: [Included("a1", "Christmas 2025"), Included("a2", "Hikes")],
            photos: new() { ["a1"] = [Photo("p1")] });

        var snapshot = await h.Library.GetAsync(default);

        Assert.Equal(["p1"], snapshot.Photos.Select(p => p.AssetId));
        Assert.Equal(ImmichClient.NotFound, snapshot.Error);
    }

    [Fact]
    public async Task NothingIncludedIsAnEmptyLibrary_NotAFailure()
    {
        var h = NewHarness(albums: [Excluded("a1", "Christmas 2025")]);

        var snapshot = await h.Library.GetAsync(default);

        Assert.Empty(snapshot.Photos);
        Assert.Null(snapshot.Error);
        Assert.Empty(h.Immich.AlbumPhotoCalls);
    }

    private static ImmichAsset Photo(string id) => new(id, Now.AddYears(-1), "Boulder", "USA");

    private static PhotoAlbum Included(string id, string name) => NewAlbum(id, name, included: true);

    private static PhotoAlbum Excluded(string id, string name) => NewAlbum(id, name, included: false);

    private static PhotoAlbum NewAlbum(string id, string name, bool included) => new()
    {
        ImmichAlbumId = id,
        Name = name,
        Included = included,
        CreatedAt = Now.AddDays(-1),
        UpdatedAt = Now.AddDays(-1),
    };

    private static Harness NewHarness(
        IReadOnlyList<PhotoAlbum>? albums = null,
        Dictionary<string, IReadOnlyList<ImmichAsset>>? photos = null)
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<PhotosContext>().UseInMemoryDatabase(dbName).Options;

        using (var seedDb = new PhotosContext(options))
        {
            seedDb.Albums.AddRange(albums ?? []);
            seedDb.SaveChanges();
        }

        var immich = new StubImmichClient();
        foreach (var (albumId, assets) in photos ?? [])
            immich.Photos[albumId] = ImmichListResult<ImmichAsset>.Ok(assets);

        // A real scope factory over the two services PhotoLibrary resolves -
        // it is a singleton reaching into a scope, and faking that away would
        // stop the test covering the part most likely to break in DI.
        var services = new ServiceCollection();
        services.AddDbContext<PhotosContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IImmichClient>(immich);
        var provider = services.BuildServiceProvider();

        var time = new FakeTimeProvider(Now);
        var library = new PhotoLibrary(
            provider.GetRequiredService<IServiceScopeFactory>(), time, NullLogger<PhotoLibrary>.Instance);

        return new Harness(library, immich, time, () => new PhotosContext(options));
    }

    private sealed record Harness(
        PhotoLibrary Library, StubImmichClient Immich, FakeTimeProvider Time, Func<PhotosContext> NewDb);
}

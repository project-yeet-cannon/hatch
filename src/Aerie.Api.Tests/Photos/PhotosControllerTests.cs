using Aerie.Api.Modules.Photos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Photos;

/// <summary>
/// Covers the endpoints' own behaviour: that the wall gets a bounded, shuffled
/// manifest, that the image endpoint refuses anything outside the selection,
/// and that a half-configured install reads as a form rather than as an error.
/// </summary>
public class PhotosControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Status_UnconfiguredSaysSo_WithoutCallingImmich()
    {
        var h = NewHarness(baseUrl: null, apiKey: null);

        var status = await h.Controller.GetStatus(default);

        Assert.False(status.IsConfigured);
        Assert.False(status.HasApiKey);
        Assert.False(status.Reachable);
        Assert.Null(status.Error);
    }

    /// <summary>A host and a key that are merely present tell an operator nothing, so the status is a live answer from Immich.</summary>
    [Fact]
    public async Task Status_ReportsWhatImmichAnswered()
    {
        var h = NewHarness(albums: [Album("a1", "Christmas 2025", included: true), Album("a2", "Hikes")]);

        var status = await h.Controller.GetStatus(default);

        Assert.True(status.IsConfigured);
        Assert.True(status.Reachable);
        Assert.Equal("v3.0.0", status.Version);
        Assert.Equal(2, status.AlbumCount);
        Assert.Equal(1, status.IncludedAlbumCount);
    }

    /// <summary>
    /// A key scoped to exactly what Photos needs - album.read and asset.view -
    /// is refused at /api/server/about, which is guarded by Immich's own
    /// server.about permission. A red light over a working system would send an
    /// operator to re-mint a key that was already correct.
    /// </summary>
    [Fact]
    public async Task Status_AKeyThatCannotReadServerInfoButCanListAlbumsIsConnected()
    {
        var h = NewHarness();
        h.Immich.Server = ImmichResult<ImmichServer>.Failed(ImmichClient.Unauthorized);
        h.Immich.Albums = ImmichListResult<ImmichAlbum>.Ok([]);

        var status = await h.Controller.GetStatus(default);

        Assert.True(status.Reachable);
        Assert.Null(status.Error);
        // The version is what server.about was for, and it is the only thing
        // lost by not having it.
        Assert.Null(status.Version);
    }

    /// <summary>The other half: a key Immich refuses outright is reported, not thrown.</summary>
    [Fact]
    public async Task Status_AKeyRefusedEverywhereIsReportedAsRefused()
    {
        var h = NewHarness();
        h.Immich.Server = ImmichResult<ImmichServer>.Failed(ImmichClient.Unauthorized);
        h.Immich.Albums = ImmichListResult<ImmichAlbum>.Failed(ImmichClient.Unauthorized);

        var status = await h.Controller.GetStatus(default);

        Assert.True(status.IsConfigured);
        Assert.False(status.Reachable);
        Assert.Equal(ImmichClient.Unauthorized, status.Error);
    }

    /// <summary>A dead host answers every endpoint the same way, so there is nothing to learn from asking a second one.</summary>
    [Fact]
    public async Task Status_ADeadHostIsNotProbedTwice()
    {
        var h = NewHarness();
        h.Immich.Server = ImmichResult<ImmichServer>.Failed(ImmichClient.Unreachable);

        var status = await h.Controller.GetStatus(default);

        Assert.Equal(ImmichClient.Unreachable, status.Error);
        Assert.Equal(0, h.Immich.AlbumListCalls);
    }

    [Fact]
    public async Task UpdateAlbum_TogglesTheSelection_AndDropsTheCachedLibrary()
    {
        var h = NewHarness(albums: [Album("a1", "Christmas 2025")]);
        var id = (await h.Db.Albums.SingleAsync()).Id;

        var updated = Value(await h.Controller.UpdateAlbum(id, new PhotoAlbumSelectionRequest(true, null), default));

        Assert.True(updated.Included);
        Assert.True((await h.Db.Albums.AsNoTracking().SingleAsync()).Included);
        Assert.Equal(1, h.Library.Invalidations);
    }

    /// <summary>The common write is a checkbox, and it must not reset an arrangement it says nothing about.</summary>
    [Fact]
    public async Task UpdateAlbum_LeavesTheOrderAloneWhenItIsNotGiven()
    {
        var h = NewHarness(albums: [Album("a1", "Christmas 2025", sortOrder: 5)]);
        var id = (await h.Db.Albums.SingleAsync()).Id;

        var updated = Value(await h.Controller.UpdateAlbum(id, new PhotoAlbumSelectionRequest(true, null), default));

        Assert.Equal(5, updated.SortOrder);
    }

    [Fact]
    public async Task UpdateAlbum_ThatChangesNothingLeavesTheCacheAlone()
    {
        var h = NewHarness(albums: [Album("a1", "Christmas 2025", included: true, sortOrder: 5)]);
        var id = (await h.Db.Albums.SingleAsync()).Id;

        await h.Controller.UpdateAlbum(id, new PhotoAlbumSelectionRequest(true, 5), default);

        Assert.Equal(0, h.Library.Invalidations);
    }

    [Fact]
    public async Task UpdateAlbum_UnknownAlbumIs404()
    {
        var h = NewHarness();

        Assert.IsType<NotFoundResult>(
            (await h.Controller.UpdateAlbum(Guid.NewGuid(), new PhotoAlbumSelectionRequest(true, null), default)).Result);
    }

    [Fact]
    public async Task Carousel_IsBoundedByTheRequestedCount_AndSaysHowManyThereAre()
    {
        var h = NewHarness(photos: Photos(10));

        var carousel = await h.Controller.GetCarousel(3, default);

        Assert.Equal(3, carousel.Photos.Count);
        Assert.Equal(10, carousel.TotalPhotos);
        Assert.All(carousel.Photos, p => Assert.Equal("Christmas 2025", p.AlbumName));
    }

    /// <summary>A hand-typed count must not be able to ask the API to serialize a whole library.</summary>
    [Fact]
    public async Task Carousel_ClampsAnAbsurdCount()
    {
        var h = NewHarness(photos: Photos(PhotosController.MaxCarouselSize + 50));

        var carousel = await h.Controller.GetCarousel(100_000, default);

        Assert.Equal(PhotosController.MaxCarouselSize, carousel.Photos.Count);
    }

    [Fact]
    public async Task Carousel_DrawsWithoutRepeating()
    {
        var h = NewHarness(photos: Photos(20));

        var carousel = await h.Controller.GetCarousel(20, default);

        Assert.Equal(20, carousel.Photos.Select(p => p.AssetId).Distinct().Count());
    }

    /// <summary>An empty selection is a wall with no carousel on it, not an error on a dashboard.</summary>
    [Fact]
    public async Task Carousel_IsEmptyWhenNothingIsIncluded()
    {
        var h = NewHarness();

        var carousel = await h.Controller.GetCarousel(null, default);

        Assert.Empty(carousel.Photos);
        Assert.Equal(0, carousel.TotalPhotos);
    }

    /// <summary>The stale library still draws, and the reason travels with it so the kiosk can log why it is stale.</summary>
    [Fact]
    public async Task Carousel_CarriesTheLibrarysErrorWithoutLosingThePhotos()
    {
        var h = NewHarness(photos: Photos(4));
        h.Library.Error = ImmichClient.Unreachable;

        var carousel = await h.Controller.GetCarousel(null, default);

        Assert.Equal(4, carousel.Photos.Count);
        Assert.Equal(ImmichClient.Unreachable, carousel.Error);
    }

    [Fact]
    public async Task Image_ServesAPhotoFromAnIncludedAlbum()
    {
        var h = NewHarness(photos: Photos(2));

        var result = Assert.IsType<FileContentResult>(await h.Controller.GetAssetImage("p0", null, default));

        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal([1, 2, 3], result.FileContents);
        Assert.Equal(("p0", ImmichImageSizes.Preview), h.Immich.ImageCalls.Single());
        Assert.Contains("private", h.Controller.Response.Headers.CacheControl.ToString());
    }

    /// <summary>
    /// The one that matters. Without the allow-list this endpoint is a hole
    /// straight through to every photo in the house, and Immich is never even
    /// asked for one outside the selection.
    /// </summary>
    [Fact]
    public async Task Image_RefusesAnAssetOutsideTheIncludedAlbums()
    {
        var h = NewHarness(photos: Photos(2));

        Assert.IsType<NotFoundResult>(await h.Controller.GetAssetImage("somebody-elses-photo", null, default));
        Assert.Empty(h.Immich.ImageCalls);
    }

    [Fact]
    public async Task Image_RefusesASizeThatIsNotARendition()
    {
        var h = NewHarness(photos: Photos(2));

        Assert.IsType<BadRequestObjectResult>(await h.Controller.GetAssetImage("p0", "original", default));
        Assert.Empty(h.Immich.ImageCalls);
    }

    /// <summary>A photo deleted in Immich since the library was cached is an ordinary event on a wall that cycles for hours, not a 502.</summary>
    [Fact]
    public async Task Image_APhotoDeletedUpstreamIs404()
    {
        var h = NewHarness(photos: Photos(2));
        h.Immich.Image = ImmichResult<ImmichImage>.Failed(ImmichClient.NotFound);

        Assert.IsType<NotFoundResult>(await h.Controller.GetAssetImage("p0", null, default));
    }

    [Fact]
    public async Task Cover_ServesTheAlbumsOwnThumbnail()
    {
        var h = NewHarness(albums: [Album("a1", "Christmas 2025", cover: "cover-1")]);
        var id = (await h.Db.Albums.SingleAsync()).Id;

        Assert.IsType<FileContentResult>(await h.Controller.GetAlbumCover(id, default));
        Assert.Equal(("cover-1", ImmichImageSizes.Thumbnail), h.Immich.ImageCalls.Single());
    }

    [Fact]
    public async Task Cover_AnEmptyAlbumHasNoneAndSaysSo()
    {
        var h = NewHarness(albums: [Album("a1", "Empty")]);
        var id = (await h.Db.Albums.SingleAsync()).Id;

        Assert.IsType<NotFoundResult>(await h.Controller.GetAlbumCover(id, default));
        Assert.Empty(h.Immich.ImageCalls);
    }

    [Fact]
    public async Task Refresh_AnUnconfiguredInstallIsToldToConfigureItself()
    {
        var h = NewHarness(baseUrl: null, apiKey: null);
        h.Immich.Albums = ImmichListResult<ImmichAlbum>.Failed(ImmichClient.NotConfigured);

        Assert.IsType<BadRequestObjectResult>((await h.Controller.RefreshAlbums(default)).Result);
    }

    [Fact]
    public async Task Refresh_AProviderFailureIsABadGateway()
    {
        var h = NewHarness();
        h.Immich.Albums = ImmichListResult<ImmichAlbum>.Failed(ImmichClient.Unreachable);

        var result = Assert.IsType<ObjectResult>((await h.Controller.RefreshAlbums(default)).Result);
        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
    }

    [Fact]
    public async Task Refresh_ReportsWhatChanged()
    {
        var h = NewHarness();
        h.Immich.Albums = ImmichListResult<ImmichAlbum>.Ok([new ImmichAlbum("a1", "Christmas 2025", null, 42, null, null)]);

        var result = Value(await h.Controller.RefreshAlbums(default));

        Assert.Equal(1, result.Added);
        Assert.Equal("Christmas 2025", (await h.Db.Albums.AsNoTracking().SingleAsync()).Name);
    }

    private static LibraryPhoto[] Photos(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new LibraryPhoto($"p{i}", "Christmas 2025", Now.AddYears(-1), "Boulder", "USA"))];

    private static PhotoAlbum Album(string id, string name, bool included = false, int sortOrder = 0, string? cover = null) =>
        new()
        {
            ImmichAlbumId = id,
            Name = name,
            Included = included,
            SortOrder = sortOrder,
            ThumbnailAssetId = cover,
            CreatedAt = Now.AddDays(-1),
            UpdatedAt = Now.AddDays(-1),
        };

    private static Harness NewHarness(
        IReadOnlyList<PhotoAlbum>? albums = null,
        LibraryPhoto[]? photos = null,
        string? baseUrl = "https://photos.example.com",
        string? apiKey = "immich-api-key")
    {
        var options = new DbContextOptionsBuilder<PhotosContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using (var seedDb = new PhotosContext(options))
        {
            seedDb.Albums.AddRange(albums ?? []);
            seedDb.SaveChanges();
        }

        var db = new PhotosContext(options);
        var immich = new StubImmichClient();
        var library = new StubPhotoLibrary(photos ?? []);
        var time = new FakeTimeProvider(Now);
        var settings = new StubSiteSettings(immichBaseUrl: baseUrl, immichApiKey: apiKey);

        var controller = new PhotosController(
            db, immich,
            new PhotoAlbumService(db, immich, library, time, NullLogger<PhotoAlbumService>.Instance),
            library, settings, time)
        {
            // The image endpoints set a caching header, which needs a response
            // to set it on.
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        return new Harness(controller, db, immich, library);
    }

    private static T Value<T>(ActionResult<T> result) =>
        result.Value ?? throw new InvalidOperationException($"expected a value, got {result.Result?.GetType().Name ?? "nothing"}");

    private sealed record Harness(
        PhotosController Controller, PhotosContext Db, StubImmichClient Immich, StubPhotoLibrary Library);
}

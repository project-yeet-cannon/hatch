using System.Net;
using Aerie.Api.Modules.Photos;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aerie.Api.Tests.Photos;

/// <summary>
/// Covers the wire: that the key travels as Immich's header rather than as a
/// bearer token, that a host typed with a trailing slash or a path prefix still
/// resolves, that videos never reach a photo frame, and that every failure
/// comes back as a value rather than as an exception.
/// </summary>
public class ImmichClientTests
{
    private const string Key = "immich-api-key";

    [Fact]
    public async Task NotConfigured_WhenNoHostIsSet_AndNothingIsCalled()
    {
        var (client, handler) = NewClient(baseUrl: null, responses: Ok("[]"));

        var result = await client.ListAlbumsAsync(CancellationToken.None);

        Assert.Equal(ImmichClient.NotConfigured, result.Error);
        Assert.Empty(handler.Requests);
    }

    /// <summary>A host without a key is the half-finished setup form, and it must not produce a request either.</summary>
    [Fact]
    public async Task NotConfigured_WhenNoKeyIsSet()
    {
        var (client, handler) = NewClient(apiKey: null, responses: Ok("[]"));

        Assert.Equal(ImmichClient.NotConfigured, (await client.ListAlbumsAsync(CancellationToken.None)).Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NotConfigured_WhenTheHostIsNotAnAbsoluteHttpUrl()
    {
        var (client, handler) = NewClient(baseUrl: "photos.example.com", responses: Ok("[]"));

        Assert.Equal(ImmichClient.NotConfigured, (await client.ListAlbumsAsync(CancellationToken.None)).Error);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Immich authenticates a machine caller with x-api-key. Sending the key as
    /// an Authorization header instead would be refused, and the symptom - a
    /// 401 from a key that works in curl - is a long afternoon.
    /// </summary>
    [Fact]
    public async Task SendsTheKeyAsImmichsOwnHeader()
    {
        var (client, handler) = NewClient(responses: Ok("[]"));

        await client.ListAlbumsAsync(CancellationToken.None);

        Assert.Equal(Key, handler.Requests[0].Headers[ImmichClient.ApiKeyHeader]);
        Assert.Empty(handler.AuthorizationHeaders);
        Assert.Equal("https://photos.example.com/api/albums", handler.Requests[0].Url);
    }

    [Fact]
    public async Task TrailingSlashOnTheHostDoesNotDoubleUp()
    {
        var (client, handler) = NewClient(baseUrl: "https://photos.example.com/", responses: Ok("[]"));

        await client.ListAlbumsAsync(CancellationToken.None);

        Assert.Equal("https://photos.example.com/api/albums", handler.Requests[0].Url);
    }

    /// <summary>The case new Uri(base, "/api/albums") would silently break, by throwing the prefix away.</summary>
    [Fact]
    public async Task KeepsAPathPrefixOnTheHost()
    {
        var (client, handler) = NewClient(baseUrl: "https://home.example.com/photos", responses: Ok("[]"));

        await client.ListAlbumsAsync(CancellationToken.None);

        Assert.Equal("https://home.example.com/photos/api/albums", handler.Requests[0].Url);
    }

    [Fact]
    public async Task ReadsAlbums_WithTheCoverAndTheCount()
    {
        var (client, _) = NewClient(responses: Ok("""
            [
              {"id":"a1","albumName":"Christmas 2025","description":"the good one","assetCount":42,
               "albumThumbnailAssetId":"cover-1","updatedAt":"2026-01-02T03:04:05.000Z"},
              {"id":"a2","albumName":"Hikes","assetCount":0}
            ]
            """));

        var result = await client.ListAlbumsAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        var christmas = result.Items[0];
        Assert.Equal("a1", christmas.Id);
        Assert.Equal("Christmas 2025", christmas.Name);
        Assert.Equal("the good one", christmas.Description);
        Assert.Equal(42, christmas.AssetCount);
        Assert.Equal("cover-1", christmas.ThumbnailAssetId);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), christmas.UpdatedAt);
        Assert.Null(result.Items[1].ThumbnailAssetId);
    }

    /// <summary>A blank name is legal in Immich, and a blank row in a list you have to choose from is not usable.</summary>
    [Fact]
    public async Task AnUnnamedAlbumIsNamedForItsId()
    {
        var (client, _) = NewClient(responses: Ok("""[{"id":"a1","albumName":"   ","assetCount":1}]"""));

        Assert.Equal("a1", (await client.ListAlbumsAsync(CancellationToken.None)).Items.Single().Name);
    }

    [Fact]
    public async Task AlbumPhotos_DropVideos_AndCarryTheirCaptionMaterial()
    {
        var (client, handler) = NewClient(responses: Ok("""
            {"id":"a1","albumName":"Hikes","assets":[
              {"id":"p1","type":"IMAGE","fileCreatedAt":"2026-05-01T00:00:00.000Z",
               "localDateTime":"2026-04-30T18:12:00.000Z","exifInfo":{"city":"Boulder","country":"USA"}},
              {"id":"v1","type":"VIDEO","fileCreatedAt":"2026-05-01T00:00:00.000Z"},
              {"id":"p2","type":"IMAGE","fileCreatedAt":"2026-05-02T00:00:00.000Z"}
            ]}
            """));

        var result = await client.ListAlbumPhotosAsync("a1", CancellationToken.None);

        Assert.Equal(["p1", "p2"], result.Items.Select(a => a.Id));
        // localDateTime, not fileCreatedAt: when the photo was taken where it
        // was taken is what a caption means by "when".
        Assert.Equal(new DateTimeOffset(2026, 4, 30, 18, 12, 0, TimeSpan.Zero), result.Items[0].TakenAt);
        Assert.Equal("Boulder", result.Items[0].City);
        Assert.Equal("USA", result.Items[0].Country);
        // A scanned print with no EXIF is still a photo worth showing.
        Assert.Null(result.Items[1].City);
        Assert.Equal("https://photos.example.com/api/albums/a1", handler.Requests[0].Url);
    }

    [Fact]
    public async Task ARejectedKeyIsUnauthorized_NotAnException()
    {
        var (client, _) = NewClient(responses: new HttpResponseMessage(HttpStatusCode.Unauthorized));

        Assert.Equal(ImmichClient.Unauthorized, (await client.ListAlbumsAsync(CancellationToken.None)).Error);
    }

    [Fact]
    public async Task AnUnreachableServerIsAResult_NotAnException()
    {
        var client = NewClient(new HttpRequestException("no route to host"));

        Assert.Equal(ImmichClient.Unreachable, (await client.ListAlbumsAsync(CancellationToken.None)).Error);
    }

    /// <summary>Everything the caller does not branch on keeps its number, which is enough to put in front of an admin.</summary>
    [Fact]
    public async Task AnUnexpectedStatusKeepsItsNumber()
    {
        var (client, _) = NewClient(responses: new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        Assert.Equal("http_503", (await client.ListAlbumsAsync(CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Image_AsksImmichForTheRenditionAndCarriesItsContentType()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/webp");
        var (client, handler) = NewClient(responses: response);

        var result = await client.GetImageAsync("p1", ImmichImageSizes.Preview, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([1, 2, 3], result.Value!.Bytes);
        Assert.Equal("image/webp", result.Value.ContentType);
        Assert.Equal("https://photos.example.com/api/assets/p1/thumbnail?size=preview", handler.Requests[0].Url);
    }

    /// <summary>
    /// The originals are deliberately unreachable through this client, so a
    /// size it does not know is refused before a request is made rather than
    /// passed through to Immich to interpret.
    /// </summary>
    [Fact]
    public async Task Image_RefusesASizeItDoesNotKnow()
    {
        var (client, handler) = NewClient(responses: Ok("{}"));

        Assert.Equal(ImmichClient.NotFound, (await client.GetImageAsync("p1", "original", CancellationToken.None)).Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Image_RefusesSomethingLargerThanTheCeiling()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        response.Content.Headers.ContentLength = ImmichClient.MaxImageBytes + 1;
        var (client, _) = NewClient(responses: response);

        Assert.Equal("too_large", (await client.GetImageAsync("p1", ImmichImageSizes.Preview, CancellationToken.None)).Error);
    }

    /// <summary>The photo the wall is asking for was deleted in Immich since the library was cached - an ordinary event, and one the caller distinguishes.</summary>
    [Fact]
    public async Task Image_MissingUpstreamIsNotFound()
    {
        var (client, _) = NewClient(responses: new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Equal(ImmichClient.NotFound, (await client.GetImageAsync("p1", ImmichImageSizes.Preview, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Server_ReportsTheVersionFromAKeyedEndpoint()
    {
        var (client, handler) = NewClient(responses: Ok("""{"version":"v3.0.0"}"""));

        var result = await client.GetServerAsync(CancellationToken.None);

        Assert.Equal("v3.0.0", result.Value!.Version);
        // /api/server/about, not /api/server/ping: ping answers without a key,
        // so a "test connection" built on it goes green for a wrong key.
        Assert.Equal("https://photos.example.com/api/server/about", handler.Requests[0].Url);
        Assert.Equal(Key, handler.Requests[0].Headers[ImmichClient.ApiKeyHeader]);
    }

    private static HttpResponseMessage Ok(string json) => StubHttpMessageHandler.Json(HttpStatusCode.OK, json);

    private static (ImmichClient Client, StubHttpMessageHandler Handler) NewClient(
        string? baseUrl = "https://photos.example.com",
        string? apiKey = Key,
        HttpResponseMessage? responses = null)
    {
        var handler = new StubHttpMessageHandler(responses ?? Ok("[]"));
        return (Build(handler, baseUrl, apiKey), handler);
    }

    /// <summary>A dead host, which is a transport failure rather than a status code - the case that would otherwise throw straight through a dashboard request.</summary>
    private static ImmichClient NewClient(Exception throwing) =>
        Build(new ThrowingHandler(throwing), "https://photos.example.com", Key);

    private static ImmichClient Build(HttpMessageHandler handler, string? baseUrl, string? apiKey) =>
        new(new StubHttpClientFactory(handler),
            new StubSiteSettings(immichBaseUrl: baseUrl, immichApiKey: apiKey),
            NullLogger<ImmichClient>.Instance);

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw exception;
    }
}

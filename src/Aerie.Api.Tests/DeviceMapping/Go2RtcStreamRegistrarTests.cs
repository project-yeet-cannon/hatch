using System.Net;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Tests.DeviceMapping;

/// <summary>
/// docs/plans/cameras.md Phase 11. What matters here is the shape of the
/// request - go2rtc's PUT contract was verified against the real 1.9.14 image,
/// and these pin that Aerie keeps speaking it.
/// </summary>
public class Go2RtcStreamRegistrarTests
{
    private const string RtspUrl = "rtsp://admin:p%40ss@10.0.0.9:554/h264Preview_01_sub";

    private static (Go2RtcStreamRegistrar Registrar, StubHttpMessageHandler Handler) NewRegistrar(
        HttpResponseMessage response, string baseAddress = "http://go2rtc:1984/")
    {
        var handler = new StubHttpMessageHandler(response);
        var registrar = new Go2RtcStreamRegistrar(
            new StubHttpClientFactory(handler),
            Options.Create(new CameraStreamOptions { Go2RtcBaseAddress = baseAddress }),
            NullLogger<Go2RtcStreamRegistrar>.Instance);
        return (registrar, handler);
    }

    [Fact]
    public async Task Puts_the_stream_under_the_entity_id()
    {
        var (registrar, handler) = NewRegistrar(new HttpResponseMessage(HttpStatusCode.OK));

        Assert.True(await registrar.EnsureAsync("camera.innit_fluent", RtspUrl, CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        var uri = new Uri(request.Url);
        Assert.Equal("/api/streams", uri.AbsolutePath);

        // Both values arrive intact through the query, which is the whole
        // reason each is escaped separately: the source is itself a URL, with
        // a scheme, an escaped password and slashes in it.
        var query = ParseQuery(uri.Query);
        Assert.Equal("camera.innit_fluent", query["name"]);
        Assert.Equal(RtspUrl, query["src"]);
    }

    [Fact]
    public async Task Uses_PUT_because_a_second_call_must_replace_the_producer()
    {
        var (registrar, handler) = NewRegistrar(new HttpResponseMessage(HttpStatusCode.OK));

        await registrar.EnsureAsync("camera.innit_fluent", RtspUrl, CancellationToken.None);

        Assert.Equal("PUT", Assert.Single(handler.Requests).Method);
    }

    [Fact]
    public async Task Honours_a_base_address_with_a_path_prefix()
    {
        // go2rtc behind a reverse proxy on a subpath - the same case
        // CameraStreamTarget handles for the socket.
        var (registrar, handler) = NewRegistrar(new HttpResponseMessage(HttpStatusCode.OK), "http://proxy/go2rtc");

        await registrar.EnsureAsync("camera.x", RtspUrl, CancellationToken.None);

        Assert.Equal("/go2rtc/api/streams", new Uri(Assert.Single(handler.Requests).Url).AbsolutePath);
    }

    [Fact]
    public async Task Reports_failure_when_go2rtc_refuses()
    {
        // The 400 case this feature met for real: go2rtc persists a PUT to its
        // first -config and fails when that path is read-only.
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("open /config/go2rtc.yaml: read-only file system"),
        };
        var (registrar, _) = NewRegistrar(response);

        Assert.False(await registrar.EnsureAsync("camera.x", RtspUrl, CancellationToken.None));
    }

    [Fact]
    public async Task Reports_failure_when_the_base_address_is_unusable()
    {
        var (registrar, handler) = NewRegistrar(new HttpResponseMessage(HttpStatusCode.OK), "not a url");

        Assert.False(await registrar.EnsureAsync("camera.x", RtspUrl, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Reports_failure_when_go2rtc_cannot_be_reached()
    {
        var registrar = new Go2RtcStreamRegistrar(
            new StubHttpClientFactory(new ThrowingHandler()),
            Options.Create(new CameraStreamOptions()),
            NullLogger<Go2RtcStreamRegistrar>.Instance);

        Assert.False(await registrar.EnsureAsync("camera.x", RtspUrl, CancellationToken.None));
    }

    /// <summary>Query string back to a dictionary, unescaping each value - which is the half under test.</summary>
    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }
}

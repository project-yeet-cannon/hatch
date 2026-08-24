using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>Tells go2rtc where one camera's video comes from, just before someone watches it.</summary>
public interface IGo2RtcStreamRegistrar
{
    /// <summary>
    /// Registers or replaces the stream named <paramref name="streamName"/>.
    /// Returns false when go2rtc refused or could not be reached; the caller
    /// decides whether that is fatal, and it always is except in tests.
    /// </summary>
    Task<bool> EnsureAsync(string streamName, string rtspUrl, CancellationToken ct);
}

/// <summary>
/// The write half of the camera video path (docs/camera-devices-architecture.md),
/// and the reason there is no longer a streams file in the cluster.
///
/// Verified against go2rtc 1.9.14 rather than inferred: `PUT
/// /api/streams?name=&amp;src=` registers a stream at runtime and a second PUT
/// under the same name replaces its producer, so this is an upsert and needs no
/// read-modify-write. It is called immediately before the relay socket opens
/// rather than from a reconciler or a startup pass, which is what makes a
/// go2rtc restart self-healing: the pod comes back knowing nothing, the next
/// viewer tells it what it needs, and the cost is one HTTP round trip against a
/// keyframe wait measured in seconds.
///
/// One thing verified the hard way and worth keeping written down: go2rtc
/// persists a PUT to its *first* `-config` path and answers 400 when that path
/// is read-only - while still registering the stream. So a deployment that
/// mounts every config read-only gets a working stream and a failed-looking
/// call. charts/aerie gives it a writable emptyDir as the first `-config` for
/// exactly this reason; if that ever regresses, this class starts logging
/// failures for streams that work, which is the symptom to recognise.
/// </summary>
public class Go2RtcStreamRegistrar(
    IHttpClientFactory httpClientFactory,
    IOptions<CameraStreamOptions> options,
    ILogger<Go2RtcStreamRegistrar> logger
) : IGo2RtcStreamRegistrar
{
    public const string HttpClientName = "Go2Rtc";

    public async Task<bool> EnsureAsync(string streamName, string rtspUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(options.Value.Go2RtcBaseAddress, UriKind.Absolute, out var baseUri))
        {
            logger.LogError(
                "Cameras:Go2RtcBaseAddress is not a usable absolute address: {BaseAddress}",
                options.Value.Go2RtcBaseAddress);
            return false;
        }

        // Both values are query *values*: a stream name is an HA entity id with
        // dots in it, and a source is a URL with a scheme, a password and
        // slashes in it. EscapeDataString on each, so neither can end the query
        // or start a new parameter.
        var path = baseUri.AbsolutePath.TrimEnd('/');
        var requestUri = new UriBuilder(baseUri)
        {
            Path = $"{path}/api/streams",
            Query = $"name={Uri.EscapeDataString(streamName)}&src={Uri.EscapeDataString(rtspUrl)}",
        }.Uri;

        var client = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

            using var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return true;

            // The body, not the URL. go2rtc's error text is the useful half
            // ("read-only file system" is a whole diagnosis) and the URL is the
            // half that contains the camera's password - the one string in this
            // system that must never reach a log line, since logs ship off the
            // cluster.
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError(
                "go2rtc refused the stream registration for {StreamName}: {Status} {Body}",
                streamName, (int)response.StatusCode, body.Trim());
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Unreachable or too slow. Logged with the stream name only, for
            // the same reason as above.
            logger.LogWarning(ex, "go2rtc could not be reached to register {StreamName}", streamName);
            return false;
        }
    }
}

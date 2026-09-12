using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Hatch.Api.Models.HatchRevision;

namespace Hatch.Api.Services;

/// <summary>
/// What Flux has reconciled, for <see cref="Controllers.HatchRevisionController"/>.
/// </summary>
public interface IFluxRevisionReader
{
    /// <summary>
    /// Never throws and never returns null: outside a cluster, or when the API
    /// server can't be reached, the result carries
    /// <see cref="ClusterRevisions.Unavailable"/> instead of failing the call.
    /// </summary>
    Task<ClusterRevisions> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Reads Flux's GitRepository and Kustomization status straight from the
/// Kubernetes API with the pod's own service-account credentials.
///
/// No Kubernetes client library. This needs exactly two list calls against two
/// CRDs and reads four fields out of them; the official client is a large
/// dependency to carry for that, and the in-cluster credential convention
/// (token file, CA file, two environment variables) is stable API in its own
/// right. The RBAC behind it is deliberately the narrowest thing that works -
/// get/list on those two kinds and nothing else - because this is the first
/// Kubernetes access the API has ever had.
///
/// Cached with a short TTL: an admin screen polls this, and an HTTP endpoint
/// that turns one poll into one control-plane call is a way to generate load
/// against the thing you least want to load.
/// </summary>
public class FluxRevisionReader : IFluxRevisionReader, IDisposable
{
    private const string ServiceAccountPath = "/var/run/secrets/kubernetes.io/serviceaccount";
    private const string Namespace = "flux-system";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<FluxRevisionReader> logger;
    private readonly TimeProvider time;
    private readonly HttpClient? client;
    private readonly string? unavailable;

    private readonly SemaphoreSlim gate = new(1, 1);
    private ClusterRevisions? cached;
    private DateTimeOffset cachedAt;

    public FluxRevisionReader(ILogger<FluxRevisionReader> logger, TimeProvider time)
    {
        this.logger = logger;
        this.time = time;

        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT");
        var caPath = Path.Combine(ServiceAccountPath, "ca.crt");

        // Every deployment that isn't in a cluster lands here - `make run`,
        // `docker compose up`, the test host. Not an error, and not worth a
        // warning on every startup: the endpoint reports it in band.
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(port) || !File.Exists(caPath))
        {
            unavailable = "not running in a Kubernetes cluster";
            return;
        }

        try
        {
            client = CreateClient(host, port, caPath);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException)
        {
            logger.LogWarning(ex, "Could not set up the Kubernetes client; cluster revisions will be unavailable");
            unavailable = "could not read the cluster's service-account credentials";
        }
    }

    /// <summary>
    /// Validates the API server against the cluster CA specifically, rather
    /// than against the machine's trust store (which does not contain it) or
    /// with validation off (which would make the token stealable by anything
    /// that can answer on that address).
    /// </summary>
    private static HttpClient CreateClient(string host, string port, string caPath)
    {
        var ca = X509CertificateLoader.LoadCertificateFromFile(caPath);

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                if (cert is null || chain is null) return false;

                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(cert);
            },
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{host}:{port}"),
            Timeout = Timeout,
        };
    }

    public async Task<ClusterRevisions> ReadAsync(CancellationToken ct)
    {
        if (client is null) return new ClusterRevisions([], unavailable);

        var now = time.GetUtcNow();
        if (cached is not null && now - cachedAt < CacheTtl) return cached;

        await gate.WaitAsync(ct);
        try
        {
            // Re-checked inside the gate: several callers can queue on one slow
            // control-plane call, and without this they would each fire their
            // own the moment they got in.
            if (cached is not null && time.GetUtcNow() - cachedAt < CacheTtl) return cached;

            var result = await FetchAsync(ct);
            cached = result;
            cachedAt = time.GetUtcNow();
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ClusterRevisions> FetchAsync(CancellationToken ct)
    {
        try
        {
            var sources = await GetAsync("apis/source.toolkit.fluxcd.io/v1/namespaces/{0}/gitrepositories", ct);
            var kustomizations = await GetAsync("apis/kustomize.toolkit.fluxcd.io/v1/namespaces/{0}/kustomizations", ct);

            return new ClusterRevisions(Combine(sources, kustomizations));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            // Degrade rather than fail. An endpoint whose whole job is "how
            // healthy and current is this system" must not be the thing that
            // stops answering when the system is unhealthy.
            logger.LogWarning(ex, "Could not read Flux revisions from the Kubernetes API");
            return new ClusterRevisions([], "the cluster's Flux resources could not be read");
        }
    }

    private async Task<JsonElement> GetAsync(string pathTemplate, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, string.Format(pathTemplate, Namespace));

        // Read per call rather than cached: a projected service-account token
        // is rotated by the kubelet roughly hourly, and a token cached at
        // startup stops working somewhere in the first day of uptime.
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            (await File.ReadAllTextAsync(Path.Combine(ServiceAccountPath, "token"), ct)).Trim());

        using var response = await client!.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<FluxSourceRevision> Combine(JsonElement sources, JsonElement kustomizations)
    {
        var bySource = new Dictionary<string, List<FluxKustomizationRevision>>(StringComparer.Ordinal);

        foreach (var item in Items(kustomizations))
        {
            var sourceName = item
                .Get("spec")?.Get("sourceRef")?.GetStringOrNull("name");
            if (sourceName is null) continue;

            var status = item.Get("status");
            var entry = new FluxKustomizationRevision(
                item.Get("metadata")?.GetStringOrNull("name") ?? "(unnamed)",
                ParseRevision(status?.GetStringOrNull("lastAppliedRevision")).Sha,
                ReadReady(status));

            if (!bySource.TryGetValue(sourceName, out var list)) bySource[sourceName] = list = [];
            list.Add(entry);
        }

        var result = new List<FluxSourceRevision>();
        foreach (var item in Items(sources))
        {
            var name = item.Get("metadata")?.GetStringOrNull("name") ?? "(unnamed)";
            var (branch, sha) = ParseRevision(item.Get("status")?.Get("artifact")?.GetStringOrNull("revision"));

            result.Add(new FluxSourceRevision(
                name,
                sha,
                branch,
                bySource.TryGetValue(name, out var list)
                    ? list.OrderBy(k => k.Name, StringComparer.Ordinal).ToList()
                    : []));
        }

        return result.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Flux writes a revision as <c>main@sha1:&lt;40 hex&gt;</c>. Older
    /// versions wrote <c>main/&lt;sha&gt;</c>; both are accepted so an upgrade
    /// of the controllers doesn't silently blank this out.
    /// </summary>
    public static (string? Branch, string? Sha) ParseRevision(string? revision)
    {
        if (string.IsNullOrWhiteSpace(revision)) return (null, null);

        var at = revision.IndexOf('@');
        if (at >= 0)
        {
            var branch = revision[..at];
            var rest = revision[(at + 1)..];
            var colon = rest.IndexOf(':');
            return (branch, colon >= 0 ? rest[(colon + 1)..] : rest);
        }

        var slash = revision.LastIndexOf('/');
        return slash >= 0 ? (revision[..slash], revision[(slash + 1)..]) : (null, revision);
    }

    private static bool? ReadReady(JsonElement? status)
    {
        if (status?.Get("conditions") is not { ValueKind: JsonValueKind.Array } conditions) return null;

        foreach (var condition in conditions.EnumerateArray())
        {
            if (condition.GetStringOrNull("type") == "Ready")
                return condition.GetStringOrNull("status") == "True";
        }

        return null;
    }

    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        root.Get("items") is { ValueKind: JsonValueKind.Array } items
            ? items.EnumerateArray()
            : [];

    public void Dispose()
    {
        client?.Dispose();
        gate.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Null-tolerant reads over an untyped Kubernetes response. Every field this
/// reader wants is optional in practice - a resource that has never reconciled
/// has no status at all - so the alternative is a TryGetProperty ladder at
/// every level.
/// </summary>
internal static class JsonElementExtensions
{
    public static JsonElement? Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    public static JsonElement? Get(this JsonElement? element, string name) =>
        element?.Get(name);

    public static string? GetStringOrNull(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static string? GetStringOrNull(this JsonElement? element, string name) =>
        element?.GetStringOrNull(name);
}

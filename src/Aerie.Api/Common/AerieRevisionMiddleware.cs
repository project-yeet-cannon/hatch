using System.Collections.Concurrent;
using Aerie.Api.Models.AerieRevision;
using Aerie.Api.Services;

namespace Aerie.Api.Common;

/// <summary>
/// Puts <c>Aerie-Revision</c> on every response this process sends, and notices
/// when the caller's own build disagrees with it.
///
/// Registered ahead of <see cref="AuthMiddleware"/> on purpose, so a 401 and a
/// 302 to the sign-in shell carry the header too: "which replica refused me,
/// and was it running the build I think it was" is a question worth being able
/// to answer, and it is unanswerable exactly when the wall is the thing
/// misbehaving.
///
/// Set through <see cref="HttpResponse.OnStarting(Func{object, Task}, object)"/>
/// rather than assigned inline, because the handlers downstream include static
/// files and WebSocket upgrades, and a header assigned before they run is not
/// guaranteed to survive what they do to the response.
///
/// No <c>X-</c> prefix: RFC 6648 deprecated that convention in 2012, and the
/// only <c>X-Aerie-*</c> header in the codebase is Traefik's forwardAuth grant,
/// which this has nothing to do with.
///
/// **The drift observation is the measurement step of docs/plans/version.md
/// Phase 5**, and it lives here rather than in the six frontends because every
/// web app's fetch wrapper already sends its revision on *every* request. That
/// makes this a strictly better vantage point than the client-side poll the
/// plan first imagined: it sees all traffic from all apps instead of one probe
/// per app per interval, it needs no code in any of them, and it cannot itself
/// be the thing that breaks a page. Nothing acts on the verdict yet - Phase 5.3
/// is deliberately gated on a week of watching these lines, because the number
/// nobody has is how often <see cref="RevisionDrift.Ahead"/> actually happens
/// during a rollout.
/// </summary>
public class AerieRevisionMiddleware(
    RequestDelegate next,
    IAerieRevision revision,
    ILogger<AerieRevisionMiddleware> logger,
    TimeProvider time)
{
    public const string HeaderName = "Aerie-Revision";

    /// <summary>What a web app sends back about its own build (see the Vite plugin).</summary>
    public const string ClientHeaderName = "Aerie-Client-Revision";
    public const string ClientSequenceHeaderName = "Aerie-Client-Sequence";

    /// <summary>
    /// The native kiosk shell's own build, which drifts independently of the
    /// bundle it is displaying and so travels separately.
    /// </summary>
    public const string ShellHeaderName = "Aerie-Shell-Revision";

    /// <summary>
    /// How long one drifting client revision stays quiet after it is reported.
    /// A wall tablet makes a request every few seconds; without this, one stale
    /// tablet would be the loudest thing in the log index and the drift would
    /// be harder to see for having been reported so well.
    /// </summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Bounded because the key is client-supplied: a caller can put anything in
    /// the header, and an unbounded dictionary keyed on it is a memory leak
    /// with an open door. At the cap the throttle is skipped rather than the
    /// logging - reporting too often is a nuisance, reporting nothing is a
    /// missing measurement.
    /// </summary>
    private const int MaxTrackedRevisions = 256;

    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastReported = new(StringComparer.Ordinal);

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var (response, value) = ((HttpResponse, string))state;
            response.Headers[HeaderName] = value;
            return Task.CompletedTask;
        }, (context.Response, revision.Revision));

        ObserveClientDrift(context);

        return next(context);
    }

    private void ObserveClientDrift(HttpContext context)
    {
        var clientRevision = context.Request.Headers[ClientHeaderName].ToString();
        if (string.IsNullOrWhiteSpace(clientRevision)) return;

        _ = int.TryParse(context.Request.Headers[ClientSequenceHeaderName].ToString(), out var clientSequence);

        var drift = RevisionComparison.Compare(revision.Revision, revision.Sequence, clientRevision, clientSequence);
        if (drift is RevisionDrift.Current or RevisionDrift.Unknown) return;

        if (!ShouldReport(clientRevision)) return;

        // Warning rather than Information: a client that is Behind is a device
        // about to be broken by the next breaking change, and one that is Ahead
        // means a rollout is genuinely in flight - both are things an operator
        // would want surfaced rather than found.
        logger.LogWarning(
            "Client revision drift: {Drift} client={ClientRevision} seq={ClientSequence} shell={ShellRevision} path={Path}",
            drift,
            clientRevision,
            clientSequence,
            context.Request.Headers[ShellHeaderName].ToString() is { Length: > 0 } shell ? shell : null,
            context.Request.Path.Value);
    }

    private bool ShouldReport(string clientRevision)
    {
        var now = time.GetUtcNow();

        if (LastReported.Count >= MaxTrackedRevisions && !LastReported.ContainsKey(clientRevision)) return true;

        var reported = false;
        LastReported.AddOrUpdate(
            clientRevision,
            _ =>
            {
                reported = true;
                return now;
            },
            (_, previous) =>
            {
                if (now - previous < ReportInterval) return previous;
                reported = true;
                return now;
            });

        return reported;
    }
}

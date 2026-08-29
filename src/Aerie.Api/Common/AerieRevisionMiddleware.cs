using Aerie.Api.Services;

namespace Aerie.Api.Common;

/// <summary>
/// Puts <c>Aerie-Revision</c> on every response this process sends.
///
/// Registered ahead of <see cref="AuthMiddleware"/> on purpose, so a 401 and a
/// 302 to the sign-in shell carry it too: "which replica refused me, and was it
/// running the build I think it was" is a question worth being able to answer,
/// and it is unanswerable exactly when the wall is the thing misbehaving.
///
/// Set through <see cref="HttpResponse.OnStarting(Func{object, Task}, object)"/>
/// rather than assigned inline, because the handlers downstream include static
/// files and WebSocket upgrades, and a header assigned before they run is not
/// guaranteed to survive what they do to the response.
///
/// No <c>X-</c> prefix: RFC 6648 deprecated that convention in 2012, and the
/// only <c>X-Aerie-*</c> header in the codebase is Traefik's forwardAuth grant,
/// which this has nothing to do with.
/// </summary>
public class AerieRevisionMiddleware(RequestDelegate next, IAerieRevision revision)
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

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var (response, value) = ((HttpResponse, string))state;
            response.Headers[HeaderName] = value;
            return Task.CompletedTask;
        }, (context.Response, revision.Revision));

        return next(context);
    }
}

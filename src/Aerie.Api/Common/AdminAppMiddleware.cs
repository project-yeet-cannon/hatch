using Aerie.Api.Services.Auth;

namespace Aerie.Api.Common;

/// <summary>
/// Keeps the operator's own apps off screens that have no business rendering
/// them. The admin app was the first; Hatch is the second, and the boundary is
/// the same one rather than a second copy of it.
///
/// The bundle is the boundary here, not the data behind it - every verb that
/// matters carries <see cref="RequireAdminAttribute"/> of its own, so a copy of
/// the JavaScript would buy an attacker nothing. What this buys is the far more
/// ordinary thing: a household where the operator's tools are not one tap away
/// on a family member's phone, and a page that never renders half-loaded with a
/// column of 403s in it.
///
/// A flat <c>404</c>, and that is the interesting choice. A <c>403</c> here
/// would confirm that an admin app exists at this path and that the caller is
/// simply not welcome in it, which is an invitation to go looking; a 404 is
/// indistinguishable from an install that was built without the bundle, which
/// several are - Program.cs mounts each SPA only if its directory is present.
/// The API's refusals are 403s for exactly the inverse reason
/// (<see cref="RequireAdminAttribute"/>).
///
/// Registered immediately after <see cref="AuthMiddleware"/> and before the
/// /apps static file handlers, because a path check in middleware is the only
/// place that covers both halves of how this app is served: the static assets
/// under /apps/admin *and* the MapFallbackToFile route that answers every
/// client-side deep link beneath it. Guarding one and not the other would leave
/// /apps/admin/devices serving index.html to anyone.
/// </summary>
public class AdminAppMiddleware(RequestDelegate next)
{
    /// <summary>
    /// The operator-only bundles. Matched by segment, so /apps/admin and
    /// /apps/admin/devices are covered and a later /apps/administration would
    /// not be - the same rule AuthGate's allow-list uses, for the same reason.
    /// </summary>
    /// <remarks>
    /// A list rather than one path because there is more than one operator app
    /// now, and the alternative - a second middleware beside this one - would
    /// be a second place to get the 404-not-403 reasoning right. Adding an
    /// entry here is the whole of gating a new operator bundle; the family
    /// apps are not on it and must not be, because their authorization is
    /// ownership rather than a role (Modules/README.md).
    /// </remarks>
    private static readonly PathString[] GatedApps = ["/apps/admin", "/apps/hatch"];

    public async Task InvokeAsync(HttpContext context, IAdminGate gate)
    {
        if (!gate.Enabled || !IsGated(context.Request.Path))
        {
            await next(context);
            return;
        }

        var decision = await gate.EvaluateAsync(
            context.Request.Method,
            context.Request.Path,
            context.Connection.RemoteIpAddress?.ToString(),
            context.RequestAborted);

        if (decision.IsAllowed)
        {
            await next(context);
            return;
        }

        // Same reasoning as the wall's refusal: a cached one outlives the
        // change that fixes it, and "I made her an admin and her phone still
        // says the page doesn't exist" is not a symptom anyone will connect to
        // a cache entry.
        //
        // No log line of its own: AdminGate already emitted the Warning, with
        // the reason and the grant on it. One refusal, one line - a second one
        // here would double every entry an operator greps for.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static bool IsGated(PathString path)
    {
        foreach (var app in GatedApps)
        {
            if (path.StartsWithSegments(app, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}

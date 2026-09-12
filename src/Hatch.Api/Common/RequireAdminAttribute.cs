using Hatch.Api.Models.Auth;
using Hatch.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Hatch.Api.Common;

/// <summary>
/// Marks an action - or a whole controller - as one only an administrator may
/// take. Runs as an MVC authorization filter, so it refuses before model
/// binding and before the action's own body, which is what keeps a guarded
/// endpoint from doing half its work.
///
/// Opt-in, deliberately, and that is a statement about scope rather than a
/// shortcut. Nothing in Hatch was gated on a person until now, and the honest
/// version of this first step is a constraint placed where a boundary already
/// exists - the admin app's own verbs - with everything else left exactly as
/// open as it was. A deny-by-default filter with an <c>[AllowAnonymous]</c>
/// escape would have been the same amount of code and a much larger claim: it
/// would say the household's apps have been audited for what a family member
/// may do, and they have not been, because there is no permission model yet to
/// audit them against (docs/auth-architecture.md, "A person is an authorization
/// input").
///
/// The refusal is a <c>403</c>, not the <c>404</c> the admin app's bundle gets.
/// The two differ because what they can conceal differs: a bundle that 404s is
/// indistinguishable from an install that never deployed one, while the
/// entities behind these verbs are already listed by unguarded GETs, so there
/// is nothing left for a 404 to hide and a great deal for it to confuse.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireAdminAttribute : Attribute, IAsyncAuthorizationFilter
{
    /// <summary>
    /// The one scope this route will accept from an API key, or null - the
    /// default, and the right default - for operators only.
    ///
    /// Opt-in for the same reason the attribute itself is: naming a scope here
    /// is a claim that this surface has been thought through for a caller that
    /// is a program rather than a person, and the honest number of surfaces
    /// that have been is small. Everything unnamed keeps refusing keys, which
    /// is what makes adding a key a bounded act rather than a broad one.
    ///
    /// It widens nothing for a person: a route that accepts a scope is still
    /// closed to a household member who is not an administrator.
    /// </summary>
    public string? AcceptScope { get; init; }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;

        // Resolved from the request rather than injected: an attribute's
        // constructor arguments have to be compile-time constants, so the
        // alternative is a ServiceFilter indirection that buys nothing here.
        var gate = http.RequestServices.GetRequiredService<IAdminGate>();

        var decision = await gate.EvaluateAsync(
            http.Request.Method,
            http.Request.Path,
            http.Connection.RemoteIpAddress?.ToString(),
            AcceptScope,
            http.RequestAborted);

        if (decision.IsAllowed) return;

        // The reason travels, and it is not a leak: the caller is already
        // authenticated, and the two refusals it distinguishes want different
        // sentences on screen - "this device is not linked to anyone" is fixed
        // on the Sessions page, "you are not an administrator" is fixed by
        // someone else.
        context.Result = new ObjectResult(new AuthErrorDto(decision.Reason!))
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }
}

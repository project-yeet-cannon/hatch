using Microsoft.AspNetCore.Http;

namespace Aerie.Api.Services.Auth;

/// <summary>
/// The one place the grant cookie is read, written and expired. Both callers of
/// the gate touch it - the middleware re-issues on the sliding window, the
/// controller writes it at redemption and clears it at sign-out - and a cookie
/// deleted with different attributes than it was set with is not deleted at
/// all, so the attributes are built once here rather than at each call site.
/// </summary>
public static class AuthCookie
{
    /// <summary>
    /// What we ask for, knowing we won't get it. Chrome clamps cookie Max-Age
    /// to 400 days no matter what is sent (and other browsers are heading the
    /// same way), so "permanent until revoked" is a server-side property that
    /// the browser cannot be made to honour. Asking for exactly the cap rather
    /// than something absurd keeps the sent value and the stored value the
    /// same, which is one less thing to be confused by in devtools; the
    /// sliding re-issue in AuthMiddleware is what actually makes an in-use
    /// device permanent.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(400);

    /// <summary>The token this request presented, or null. Empty is null: a cleared cookie can arrive as a present-but-blank value.</summary>
    public static string? Read(HttpRequest request, AuthOptions options) =>
        request.Cookies.TryGetValue(options.CookieName, out var token) && !string.IsNullOrEmpty(token)
            ? token
            : null;

    public static void Issue(HttpResponse response, AuthOptions options, string token) =>
        response.Cookies.Append(options.CookieName, token, Attributes(options, MaxAge));

    /// <summary>Expires the cookie. Attributes must match <see cref="Issue"/> exactly or the browser keeps the original alongside the tombstone.</summary>
    public static void Clear(HttpResponse response, AuthOptions options) =>
        response.Cookies.Delete(options.CookieName, Attributes(options, maxAge: null));

    private static CookieOptions Attributes(AuthOptions options, TimeSpan? maxAge) => new()
    {
        // The Domain attribute is the entire single-sign-on story: ".example.com"
        // is what makes one enrollment cover home., kiosk. and share. Empty
        // leaves the cookie host-only, which is the only sane value on
        // localhost - and why the name has to be overridden there too, since
        // __Secure- is a promise about the whole cookie, not just its scope.
        Domain = string.IsNullOrWhiteSpace(options.CookieDomain) ? null : options.CookieDomain.Trim(),
        Path = "/",
        HttpOnly = true,
        Secure = true,
        // Lax rather than Strict: a grant that a printed QR label or a link
        // from someone's messages cannot carry is a grant that looks revoked
        // every time it is used the way this app is meant to be used. Lax still
        // withholds the cookie from cross-site POSTs, which is the CSRF case
        // Strict is actually for.
        SameSite = SameSiteMode.Lax,
        MaxAge = maxAge,
        // Nothing here is optional-by-consent: without it there is no app.
        IsEssential = true,
    };
}

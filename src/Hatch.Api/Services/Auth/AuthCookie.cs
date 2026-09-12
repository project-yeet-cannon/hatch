using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Hatch.Api.Services.Auth;

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

    /// <summary>
    /// Every token this request presented under the cookie name, in the order
    /// the browser sent them. Usually one; the plural is the entire point.
    ///
    /// A browser will happily hold several cookies of the same name at once -
    /// cookie identity is (name, domain, path), so a host-only
    /// `home.example.com` cookie and a domain-wide `.example.com` one are two
    /// different cookies that both get sent, in one header, on requests to that
    /// host. <see cref="HttpRequest.Cookies"/> is a dictionary and silently
    /// collapses them to the last one, which is why this parses the raw header
    /// instead: with a stale duplicate present, the dictionary can hand back
    /// the dead one and the request is refused `unknown_grant` while a
    /// perfectly good grant sits in the same header, unread.
    ///
    /// Found live on 2026-08-22 (docs/auth-architecture.md), the day after the
    /// wall went up: a phone that had signed in while Auth:CookieDomain was not
    /// yet supplied, and whose cookie was therefore host-only, held exactly
    /// that pair. It authenticated on kiosk. and
    /// bounced forever on home. - the sign-in loop that has no exit, because
    /// every successful redemption added a cookie that was then ignored.
    /// </summary>
    public static IReadOnlyList<string> ReadAll(HttpRequest request, AuthOptions options)
    {
        var header = request.Headers.Cookie;
        if (StringValues.IsNullOrEmpty(header)) return [];

        // ParseList over TryParseList: a malformed pair somewhere else in the
        // header must not take our cookie down with it, and the lenient
        // overload skips what it cannot read rather than failing the lot.
        var cookies = CookieHeaderValue.ParseList(header);
        List<string>? tokens = null;

        foreach (var cookie in cookies)
        {
            if (!string.Equals(cookie.Name.Value, options.CookieName, StringComparison.Ordinal)) continue;

            // Empty is not a credential: a cleared cookie arrives as a
            // present-but-blank value, and so does the tombstone below.
            var value = cookie.Value.Value;
            if (string.IsNullOrEmpty(value)) continue;

            (tokens ??= []).Add(value);
        }

        return tokens ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// Writes the grant cookie, and expires any host-only cookie of the same
    /// name left over from an install that ran before
    /// <see cref="AuthOptions.CookieDomain"/> was supplied.
    ///
    /// The tombstone is the cure rather than the symptom: <see cref="ReadAll"/>
    /// makes the wall immune to a stale duplicate, but the duplicate itself
    /// would otherwise sit in the browser for the 400-day life of the cookie,
    /// shadowing the good one for anything else that reads it. Sending both
    /// headers on the same response costs nothing and clears it the first time
    /// the device is seen. It is skipped when there is no cookie domain,
    /// because then the cookie we are issuing *is* the host-only one and the
    /// tombstone would delete what we just set.
    /// </summary>
    public static void Issue(HttpResponse response, AuthOptions options, string token)
    {
        response.Cookies.Append(options.CookieName, token, Attributes(options, MaxAge));

        if (!string.IsNullOrWhiteSpace(options.CookieDomain))
        {
            // Appended rather than sent through Cookies.Delete, which does not
            // just add a tombstone: it first strips any Set-Cookie already on
            // the response with the same name, and it compares on the name
            // alone. That deletes the cookie appended one line above and the
            // response goes out carrying the tombstone and nothing else - which
            // signs the device out on the very request that just signed it in.
            var tombstone = Attributes(options, maxAge: TimeSpan.Zero);
            tombstone.Domain = null;
            tombstone.Expires = DateTimeOffset.UnixEpoch;
            response.Cookies.Append(options.CookieName, string.Empty, tombstone);
        }
    }

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
        SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
        MaxAge = maxAge,
        // Nothing here is optional-by-consent: without it there is no app.
        IsEssential = true,
    };
}

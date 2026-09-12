using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Hatch.Api.Services.Auth;

/// <summary>
/// What a refusal looks like on the wire, shared by the in-process middleware
/// and the forwardAuth endpoint so the two cannot drift.
///
/// The content negotiation here matters more than its size suggests. A 302
/// returned to an XHR or a `fetch` is invisible to the caller: the browser
/// follows it, the sign-in shell's HTML comes back with a 200, and the calling
/// code reports a JSON parse error or a CORS failure somewhere unrelated to
/// authentication. That is the single most common way a gate like this wastes
/// an afternoon, so document requests get the redirect and everything else -
/// XHR, fetch, a Sonos GET, a probe - gets a bare 401 it can actually see.
/// </summary>
public static class AuthChallenge
{
    /// <summary>The grant behind a request, echoed by the forwardAuth endpoint for Traefik's authResponseHeaders.</summary>
    public const string GrantHeader = "X-Hatch-Grant";

    /// <summary>The grant's label, same path. Eventually what logs./status. read as a proxy-authenticated user (docs/auth-architecture.md, Deferred).</summary>
    public const string LabelHeader = "X-Hatch-Label";

    private const string SecFetchMode = "Sec-Fetch-Mode";
    private const string RequestedWith = "X-Requested-With";

    /// <summary>Whether this caller can see a 302 - i.e. whether it is a browser navigating to a document.</summary>
    public static bool PrefersRedirect(IHeaderDictionary headers, string? method)
    {
        // A redirect a POST cannot follow without dropping its body is worse
        // than a 401 it can report, so only safe methods are ever bounced.
        if (!HttpMethods.IsGet(method ?? string.Empty) && !HttpMethods.IsHead(method ?? string.Empty)) return false;

        // Every browser that matters states outright what kind of request this
        // is, on the request itself, rather than leaving it to be inferred from
        // Accept. Believe it: "navigate" is a document, and "cors" /
        // "same-origin" / "no-cors" are fetches a 302 would silently swallow.
        if (headers.TryGetValue(SecFetchMode, out var mode) && !StringValues.IsNullOrEmpty(mode))
            return string.Equals(mode.ToString(), "navigate", StringComparison.OrdinalIgnoreCase);

        // XMLHttpRequest from anything older, and from the libraries that still set it.
        if (headers.ContainsKey(RequestedWith)) return false;

        // Last resort: what the caller says it can read. A document asks for
        // text/html; fetch() defaults to */* and a Sonos GET asks for audio.
        return headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where a refused document request is sent, carrying where it was going.</summary>
    public static string SignInLocation(AuthOptions options, string? returnTo)
    {
        var target = SafeReturnTo(returnTo);
        return target is null
            ? options.SignInPath
            : QueryHelpers.AddQueryString(options.SignInPath, "r", target);
    }

    /// <summary>
    /// The same location made absolute against the host the caller was actually
    /// going to - the form the forwardAuth endpoint has to return, and the one
    /// place the relative rule below does not hold.
    ///
    /// Traefik never hands the browser what that endpoint writes. It resolves a
    /// redirect from the auth server against its *own* request to that server,
    /// so a relative Location leaves the cluster as
    /// <c>http://api.&lt;ns&gt;.svc.cluster.local:8080/apps/auth/</c> - an
    /// in-cluster address nothing on the LAN can reach, which presents as the
    /// wall working (a 302, on time, carrying the right ?r=) and the sign-in
    /// page never loading. An absolute Location is passed through untouched.
    ///
    /// The origin is rebuilt from X-Forwarded-Proto/-Host so kiosk. still stays
    /// on kiosk., and it is trusted only inside the cookie's own domain: a Host
    /// header naming anywhere else falls back to the relative form rather than
    /// turning the one page everybody is trained to trust into an open
    /// redirect. Traefik routes by Host rule and would not have asked about a
    /// host outside the chart's Ingresses, so this is depth, not the only
    /// check - but it is one header away from being this page's worst bug.
    /// </summary>
    public static string SignInLocation(AuthOptions options, string? returnTo, string? forwardedProto, string? forwardedHost)
    {
        var relative = SignInLocation(options, returnTo);
        var origin = TrustedOrigin(options, forwardedProto, forwardedHost);
        return origin is null ? relative : origin + relative;
    }

    /// <summary>
    /// The scheme and authority to hang the sign-in path off, or null when the
    /// forwarded host is absent, malformed, or outside
    /// <see cref="AuthOptions.CookieDomain"/> - which includes every local-dev
    /// config, where that domain is empty and there is no proxy to confuse
    /// anyway, so `make run` keeps the relative redirect it has always had.
    /// </summary>
    private static string? TrustedOrigin(AuthOptions options, string? proto, string? host)
    {
        if (string.IsNullOrEmpty(host)) return null;

        var domain = options.CookieDomain.TrimStart('.');
        if (domain.Length == 0) return null;

        var scheme = string.Equals(proto, "http", StringComparison.OrdinalIgnoreCase) ? "http" : "https";

        // Uri does the validating rather than a hand-rolled check on a header:
        // anything carrying a path, a userinfo, a fragment or a control
        // character either fails to parse or stops looking like a bare
        // authority, and either way we fall back instead of emitting it.
        if (!Uri.TryCreate($"{scheme}://{host}", UriKind.Absolute, out var origin)) return null;
        if (origin.PathAndQuery != "/") return null;
        if (!string.IsNullOrEmpty(origin.Fragment) || !string.IsNullOrEmpty(origin.UserInfo)) return null;

        return origin.Host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || origin.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)
                ? origin.GetLeftPart(UriPartial.Authority)
                : null;
    }

    /// <summary>
    /// The return URL stays a rooted, same-origin path and never becomes an
    /// absolute one. The sign-in shell is the page everybody in the house is
    /// trained to trust, which makes an open redirect through it the classic
    /// own-goal; keeping ?r= relative means there is nothing for the shell to
    /// get wrong beyond re-checking the same shape.
    ///
    /// It is also why the redirect is relative on the wire: a request to
    /// kiosk.&lt;domain&gt; bounces to kiosk.&lt;domain&gt;/apps/auth/, not to
    /// home., and the cookie's Domain attribute makes the resulting grant work
    /// on both anyway.
    /// </summary>
    public static string? SafeReturnTo(string? target)
    {
        if (string.IsNullOrEmpty(target) || target[0] != '/') return null;

        // "//evil.example.com" is a protocol-relative URL and "/\evil.example.com"
        // is the same thing to browsers that fold the backslash - both resolve
        // to another origin despite starting with a slash.
        if (target.Length > 1 && target[1] is '/' or '\\') return null;

        return target;
    }
}

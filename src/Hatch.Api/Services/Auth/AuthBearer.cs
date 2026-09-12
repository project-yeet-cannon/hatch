using Microsoft.AspNetCore.Http;

namespace Hatch.Api.Services.Auth;

/// <summary>
/// The other way a credential arrives: <c>Authorization: Bearer hatch_ak_…</c>,
/// which is how a program presents an <see cref="Hatch.Api.Ef.EfApiKey"/>.
///
/// Its own file beside <see cref="AuthCookie"/> for the same reason that one
/// exists: reading a credential off a request is a job with edges, and a second
/// copy of it somewhere else is a second set of edges to get wrong. Every
/// caller that needs a bearer - the middleware, the forwardAuth endpoint, and
/// the caller-identity fallback for when the wall is switched off - goes
/// through here.
/// </summary>
public static class AuthBearer
{
    private const string Scheme = "Bearer ";

    /// <summary>
    /// The secret this request presented, or null. Only the <c>Bearer</c>
    /// scheme is read: a <c>Basic</c> header is somebody's proxy or a mistake,
    /// and answering it would be the wall inventing a credential format it does
    /// not have a table for.
    /// </summary>
    /// <remarks>
    /// The header is taken as sent, without normalizing case or trimming
    /// interior characters. A secret is compared by hash, so a value that was
    /// mangled in transit has to fail rather than be repaired into something
    /// that matches - "helpfully" cleaning it up is how a gate starts accepting
    /// strings nobody minted.
    /// </remarks>
    public static string? Read(HttpRequest request)
    {
        foreach (var header in request.Headers.Authorization)
        {
            if (header is null) continue;

            var value = header.AsSpan().TrimStart();
            if (!value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) continue;

            var secret = value[Scheme.Length..].Trim();
            if (!secret.IsEmpty) return secret.ToString();
        }

        return null;
    }
}

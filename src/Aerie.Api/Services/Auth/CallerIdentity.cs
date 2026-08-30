using Aerie.Api.Common;
using Aerie.Api.Ef;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Services.Auth;

/// <summary>
/// Who is making this request - the one way anything outside
/// <c>Services/Auth/</c> is allowed to ask.
///
/// It exists because a module needed an answer. The wall authenticates a
/// device, and for four modules that was the end of it: nothing branched on who
/// was holding the phone. Quill's notes belong to a person
/// (docs/quill.md), so the question finally has a caller - and the shape of
/// the answer matters more than the answer, because whatever asks next inherits
/// it.
/// </summary>
/// <remarks>
/// Two things it deliberately hides. The first is the cookie: a module that
/// reads <see cref="AuthCookie"/> and <see cref="AuthOptions"/> for itself is a
/// second implementation of "who is this", and the day the ceremony changes
/// (a passkey, a header from a native shell) it is a second implementation that
/// has to be found. The second is the wall's own switch. <c>Auth:Enabled</c> is
/// false in local development and under <c>AUTH_MODE=none</c>, so
/// <see cref="AuthMiddleware"/> never runs and never attaches a grant - but the
/// browser is still holding a perfectly good cookie, and "who am I" has to
/// answer the same way either way. That fallback used to be a private method on
/// AuthController for exactly this reason; it is here now so there is one copy
/// of it rather than one per asker.
/// </remarks>
public interface ICallerIdentity
{
    /// <summary>
    /// The grant behind this request, or null when there isn't one - an
    /// unenrolled browser, an allow-listed path reached before enrollment, or a
    /// background request with no HTTP context at all.
    /// </summary>
    Task<EfAuthGrant?> GrantAsync(CancellationToken ct);

    /// <summary>
    /// Whose device this is, and null for a device nobody has claimed. The two
    /// nulls - no grant, and a grant with no person - are deliberately the same
    /// answer here: a caller that wants a person has nobody either way, and
    /// distinguishing them is how a refusal turns into two refusals that leak
    /// which one happened.
    /// </summary>
    Task<Guid?> PersonIdAsync(CancellationToken ct);

    /// <summary>
    /// The person themselves rather than their key - for the one caller that
    /// needs a column off that row instead of something to compare a foreign
    /// key against.
    ///
    /// That caller is <see cref="AdminGate"/>, and the distinction is the whole
    /// reason this method exists next to the one above. Ownership scoping wants
    /// an id, because <c>WHERE PersonId = @me</c> is the safe shape and a loaded
    /// entity would only tempt someone into filtering in memory. An
    /// authorization *role* wants the row, because the answer is a property on
    /// it.
    /// </summary>
    Task<EfPerson?> PersonAsync(CancellationToken ct);
}

/// <summary>
/// Resolves the caller once per request and remembers it. Scoped, so the
/// memoization is per request and the verification below happens at most once
/// however many times a controller asks.
/// </summary>
public class CallerIdentity(
    IHttpContextAccessor accessor,
    IAuthService auth,
    IOptions<AuthOptions> options) : ICallerIdentity
{
    private readonly AuthOptions options = options.Value;

    private EfAuthGrant? grant;
    private bool resolved;

    public async Task<EfAuthGrant?> GrantAsync(CancellationToken ct)
    {
        if (resolved) return grant;

        var context = accessor.HttpContext;
        if (context is not null)
        {
            // The middleware has usually already done this work and attached the
            // result; the cookie read behind it is the wall-is-off path.
            grant = context.GetAuthGrant()
                ?? (await auth.VerifyAsync(
                    AuthCookie.ReadAll(context.Request, options),
                    context.Connection.RemoteIpAddress?.ToString(),
                    ct))?.Grant;
        }

        resolved = true;
        return grant;
    }

    public async Task<Guid?> PersonIdAsync(CancellationToken ct) => (await GrantAsync(ct))?.PersonId;

    /// <summary>
    /// Reads the navigation property rather than issuing a second query, which
    /// makes <see cref="AuthService.VerifyAsync"/>'s <c>Include(g =&gt; g.Person)</c>
    /// load-bearing: this is the same row the wall already loaded, on the
    /// hottest path in the app, and asking the database again for something it
    /// just handed us would be a per-request join to answer a question most
    /// requests never ask.
    ///
    /// A grant with a <see cref="EfAuthGrant.PersonId"/> and no
    /// <see cref="EfAuthGrant.Person"/> would therefore read as "nobody" - so
    /// every producer of a grant here goes through VerifyAsync, and
    /// AuthServiceTests pins the include.
    /// </summary>
    public async Task<EfPerson?> PersonAsync(CancellationToken ct) => (await GrantAsync(ct))?.Person;
}

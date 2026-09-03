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
    /// The API key this request presented, or null - which is every request a
    /// browser makes.
    ///
    /// Read here rather than off the header by whoever is curious, for the same
    /// reason <see cref="GrantAsync"/> exists: one implementation of "who is
    /// this", so the day the ceremony changes there is one place to change it.
    /// </summary>
    Task<EfApiKey?> ApiKeyAsync(CancellationToken ct);

    /// <summary>
    /// The name to write into an audit row: the person holding the device, else
    /// the API key's own name, else the literal <c>operator</c>.
    ///
    /// A name rather than a foreign key, and the same column takes all three,
    /// because a trail should still read after the row it named is gone - and
    /// because "Claude" and "Nathan" belong in one column if the question the
    /// trail answers is "who did this".
    ///
    /// The last case is not a fallback so much as the ordinary state of local
    /// development: <c>Auth:Enabled</c> is false, nobody is enrolled, and
    /// <c>operator</c> is the honest name for whoever is sitting at the
    /// machine.
    /// </summary>
    Task<string> ActorNameAsync(CancellationToken ct);

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

    /// <summary>What <see cref="ActorNameAsync"/> answers when nobody is holding the phone. See the interface.</summary>
    public const string Unattributed = "operator";

    private EfAuthGrant? grant;
    private bool resolved;

    private EfApiKey? apiKey;
    private bool keyResolved;

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

    /// <summary>
    /// The key the middleware authenticated, or - when the wall is switched
    /// off - the one this request's own Authorization header names.
    ///
    /// The fallback is the same shape <see cref="GrantAsync"/> takes and exists
    /// for the same reason, with one added consequence worth stating: it is
    /// what makes a bearer key work against <c>make run</c>, where
    /// <c>Auth:Enabled</c> is false and no middleware ever looked at a header.
    /// An agent developing against a local API is the first user of this lane.
    /// </summary>
    public async Task<EfApiKey?> ApiKeyAsync(CancellationToken ct)
    {
        if (keyResolved) return apiKey;

        var context = accessor.HttpContext;
        if (context is not null)
        {
            apiKey = context.GetApiKey()
                ?? await auth.VerifyApiKeyAsync(AuthBearer.Read(context.Request), ct);
        }

        keyResolved = true;
        return apiKey;
    }

    /// <summary>
    /// The person first, because a person holding a device is the more specific
    /// answer: a request cannot carry both a grant and a key, but the wall
    /// being off makes both fallbacks live at once, and a browser signed in as
    /// somebody is who that request is from.
    /// </summary>
    public async Task<string> ActorNameAsync(CancellationToken ct)
    {
        if ((await PersonAsync(ct))?.Name is { Length: > 0 } person) return person;
        if ((await ApiKeyAsync(ct))?.Name is { Length: > 0 } key) return key;

        return Unattributed;
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

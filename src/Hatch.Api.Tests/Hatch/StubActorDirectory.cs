using Hatch.Api.Services.Auth;

namespace Hatch.Api.Tests.Hatch;

/// <summary>
/// Who the house knows, in a test: whatever the test says, and nobody by
/// default.
/// </summary>
/// <remarks>
/// <para>Shared rather than private to one file because every Hatch controller
/// that hands out an issue now takes one, and thirteen copies of "return an
/// empty list" would be thirteen places to change the day the interface grows a
/// method.</para>
///
/// <para>It holds the same invariant the implementation does and holds it the
/// same way: <see cref="ResolveAsync"/> answers from the list
/// <see cref="LiveAsync"/> hands out, so a test that adds a person to the
/// directory and a test that assigns an issue to somebody who is not in it are
/// exercising the two halves of the liveness rule rather than two unrelated
/// stubs. Leaving somebody out is how a deleted person and a revoked key are
/// written here - which is precisely what they are, once the predicate has
/// run.</para>
/// </remarks>
public sealed class StubActorDirectory : IActorDirectory
{
    /// <summary>Everybody the directory knows about, in the order it will hand them back.</summary>
    public List<Actor> Live { get; } = [];

    /// <summary>Who is making the request, as an actor. Null is the signed-out state.</summary>
    public Actor? Me { get; set; }

    /// <summary>Adds a person and answers with them, so a test can name one in a single expression.</summary>
    public Actor AddPerson(string name, Guid? id = null)
    {
        var actor = new Actor(ActorKind.Person, id ?? Guid.NewGuid(), name);
        Live.Add(actor);
        return actor;
    }

    /// <summary>The same for a key. A revoked key is one this was never called for.</summary>
    public Actor AddKey(string name, Guid? id = null)
    {
        var actor = new Actor(ActorKind.Key, id ?? Guid.NewGuid(), name);
        Live.Add(actor);
        return actor;
    }

    public Task<IReadOnlyList<Actor>> LiveAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Actor>>(Live);

    public Task<Actor?> ResolveAsync(string? kind, Guid? id, CancellationToken ct) =>
        Task.FromResult(kind is null || id is null
            ? null
            : Live.FirstOrDefault(a => a.Kind == kind && a.Id == id.Value));

    public Task<Actor?> MeAsync(CancellationToken ct) => Task.FromResult(Me);
}

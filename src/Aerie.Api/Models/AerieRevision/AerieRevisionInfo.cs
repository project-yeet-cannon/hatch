namespace Aerie.Api.Models.AerieRevision;

/// <summary>
/// What commit this replica is running, and - when the caller identified
/// itself - how the caller's own build compares. See docs/plans/version.md.
/// </summary>
/// <param name="Revision">Full git sha, or "dev" for an unstamped build.</param>
/// <param name="Sequence">Commit count at build time; the ordering. 0 when unstamped.</param>
/// <param name="BuiltAt">Approximately when this binary was written.</param>
/// <param name="Client">
/// The verdict on the caller's build, or null if it sent no
/// <c>Aerie-Client-Revision</c> - which is every caller that isn't one of
/// Aerie's own web apps, and every app build from before this shipped.
/// </param>
/// <param name="Cluster">
/// What Flux has reconciled, for an authenticated admin caller. Null for
/// everyone else, and null when Flux could not be read - see
/// <see cref="ClusterRevisions"/>.
/// </param>
public record AerieRevisionInfo(
    string Revision,
    int Sequence,
    DateTimeOffset? BuiltAt,
    ClientRevisionVerdict? Client = null,
    ClusterRevisions? Cluster = null);

/// <summary>
/// Where a calling client's build sits relative to this replica's.
/// </summary>
/// <param name="Revision">The sha the client reported.</param>
/// <param name="Sequence">The ordering the client reported.</param>
/// <param name="Drift">See <see cref="RevisionDrift"/>.</param>
public record ClientRevisionVerdict(string Revision, int Sequence, RevisionDrift Drift);

/// <summary>
/// The four answers, and the reason there are four rather than two.
///
/// <see cref="Ahead"/> is the case that earns this type. During a rolling
/// deploy a browser can be loaded from a new replica and then have its next
/// request answered by an old one; a client that reloads on any difference
/// will thrash between the two until the rollout finishes. Only
/// <see cref="Behind"/> may ever trigger an action.
///
/// <see cref="Unknown"/> covers a comparison that cannot be made rather than
/// one that came out equal - an unstamped build on either side, or a client
/// that sent a revision but no usable sequence. Distinguishing it from
/// <see cref="Current"/> is what keeps `npm run dev` from looking up to date.
/// </summary>
public enum RevisionDrift
{
    Unknown,
    Current,
    Behind,
    Ahead,
}

/// <summary>
/// Where one build sits relative to another. Pure, and deliberately not a
/// method on the controller: this is the rule a wall tablet's decision to
/// reload itself rests on, and it should be readable and testable without an
/// HTTP request in the picture.
/// </summary>
public static class RevisionComparison
{
    /// <param name="revision">This replica's sha.</param>
    /// <param name="sequence">This replica's commit count, or 0 if unstamped.</param>
    /// <param name="clientRevision">The caller's sha.</param>
    /// <param name="clientSequence">The caller's commit count, or 0 if unstamped.</param>
    public static RevisionDrift Compare(string revision, int sequence, string clientRevision, int clientSequence)
    {
        // Unstamped first, before identity. "dev" == "dev" is a string match
        // and not a fact about the world: two unstamped builds are two
        // different working trees, and calling them Current would tell a
        // developer their page is up to date with a server it has never
        // agreed with. An unstamped build has no identity and no position.
        if (IsUnstamped(revision, sequence) || IsUnstamped(clientRevision, clientSequence))
            return RevisionDrift.Unknown;

        // Identity decides "same". Ordering is only consulted once the shas are
        // known to differ - two builds of the same commit are the same build
        // whatever their counts say.
        if (string.Equals(clientRevision, revision, StringComparison.OrdinalIgnoreCase))
            return RevisionDrift.Current;

        if (clientSequence < sequence) return RevisionDrift.Behind;
        if (clientSequence > sequence) return RevisionDrift.Ahead;

        // Equal counts, different shas: divergent history rather than a
        // rollout - two branches at the same depth. Not an ordering this can
        // resolve, and guessing would be worse than saying so.
        return RevisionDrift.Unknown;
    }

    private static bool IsUnstamped(string revision, int sequence) =>
        sequence <= 0 || string.IsNullOrEmpty(revision) || revision == "dev";
}

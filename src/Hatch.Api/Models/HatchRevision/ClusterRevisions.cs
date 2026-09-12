namespace Hatch.Api.Models.HatchRevision;

/// <summary>
/// What Flux has actually reconciled, as opposed to what has been pushed.
/// The gap between the two is invisible today to everything except a
/// kubeconfig, and it is exactly the gap "how up to date is my system" is
/// asking about. See docs/plans/version.md, finding 8.
/// </summary>
/// <param name="Sources">
/// One entry per GitRepository - this repo (<c>flux-system</c>) and the
/// per-installation site repo (<c>hatch-site</c>).
/// </param>
/// <param name="Unavailable">
/// Set when Flux could not be read at all, with the reason. The rest of the
/// response is still served: an endpoint that fails because the cluster is
/// unhealthy has failed at the moment it was most needed.
/// </param>
public record ClusterRevisions(IReadOnlyList<FluxSourceRevision> Sources, string? Unavailable = null);

/// <summary>
/// A Flux source and the state of everything reconciling from it.
/// </summary>
/// <param name="Name">The GitRepository's name, e.g. "flux-system".</param>
/// <param name="Revision">
/// The sha of the artifact Flux has fetched - what the cluster *could* be
/// running. Parsed out of Flux's <c>main@sha1:&lt;sha&gt;</c> form.
/// </param>
/// <param name="Branch">The branch half of that same string.</param>
/// <param name="Kustomizations">What has actually been applied from it.</param>
public record FluxSourceRevision(
    string Name,
    string? Revision,
    string? Branch,
    IReadOnlyList<FluxKustomizationRevision> Kustomizations);

/// <summary>
/// One Kustomization's applied revision.
/// </summary>
/// <param name="Name">The Kustomization's name, e.g. "apps".</param>
/// <param name="AppliedRevision">
/// The sha it last successfully applied. Behind its source's revision means a
/// reconcile is in flight or stuck; equal means converged.
/// </param>
/// <param name="Ready">Flux's own Ready condition, if it reports one.</param>
public record FluxKustomizationRevision(string Name, string? AppliedRevision, bool? Ready);

using Aerie.Api.Common;
using Aerie.Api.Models.AerieRevision;
using Aerie.Api.Services;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Aerie.Api.Controllers;

/// <summary>
/// What commit this replica is running - and, for a caller that identified its
/// own build, whether that build is behind, current, or ahead.
///
/// Distinct from <see cref="AppVersionController"/>, which answers a different
/// question with a better answer for its own purpose: that one compares
/// content-hashed asset filenames, so a backend-only deploy leaves it unchanged
/// and the wall tablets don't reload for a change they can't see. This one
/// names the commit, which is what an operator watching a rollout needs and
/// what a tablet's reload decision must *not* be keyed on. Both, deliberately.
///
/// Exempt from the auth wall (see AuthGate's allow-list). It names a commit of
/// a repository that is on its way to being public, and an unauthenticated
/// client needs it precisely in order to discover it should re-authenticate
/// against a newer build.
/// </summary>
[ApiController]
[Route("api/aerie-revision")]
public class AerieRevisionController(IAerieRevision revision, IFluxRevisionReader flux, IAuthGate gate) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<AerieRevisionInfo>> Get(CancellationToken ct)
    {
        // A cached answer is a wrong answer: the entire purpose is "what is
        // running *right now*", and this is polled from devices whose HTTP
        // cache we don't otherwise control. Same call AppVersionController makes.
        Response.Headers.CacheControl = "no-store";

        // The cluster's state is for an operator, not for every device on the
        // LAN. There are no roles in this auth model - a grant is a grant - so
        // "holds a grant" is the strongest available notion of an admin caller.
        // When the wall is off entirely the API has no way to tell anyone
        // apart, and answering is consistent with how every other gated thing
        // behaves in that configuration.
        var authorized = !gate.Enabled || HttpContext.GetAuthGrant() is not null;
        var cluster = authorized ? await flux.ReadAsync(ct) : null;

        return new AerieRevisionInfo(
            revision.Revision,
            revision.Sequence,
            revision.BuiltAt,
            ReadClientVerdict(),
            cluster);
    }

    /// <summary>
    /// The caller's own build, taken from the headers its fetch wrapper adds
    /// (Aerie.Web/vite-plugin-aerie-revision.ts) rather than from query
    /// parameters - the wrapper puts them on every request already, so the
    /// drift check costs the client nothing beyond the call it was making.
    /// </summary>
    private ClientRevisionVerdict? ReadClientVerdict()
    {
        var clientRevision = Request.Headers[AerieRevisionMiddleware.ClientHeaderName].ToString();
        if (string.IsNullOrWhiteSpace(clientRevision)) return null;

        _ = int.TryParse(Request.Headers[AerieRevisionMiddleware.ClientSequenceHeaderName].ToString(), out var clientSequence);

        return new ClientRevisionVerdict(
            clientRevision,
            clientSequence,
            RevisionComparison.Compare(revision.Revision, revision.Sequence, clientRevision, clientSequence));
    }
}

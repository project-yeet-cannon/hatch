using Hatch.Api.Common;
using Hatch.Api.Ef;
using Hatch.Api.Models.Auth;
using Hatch.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Hatch.Api.Controllers;

/// <summary>
/// Minting and revoking the credentials that are not browsers - the admin API
/// Keys page, and nothing else.
///
/// Plain <c>[RequireAdmin]</c> on the whole controller, with no
/// <c>AcceptScope</c>, and that is the load-bearing line in this file: a key
/// cannot mint a key. Allowing it would make the scope system decorative, since
/// any key could quietly issue itself a second one carrying whatever it liked.
/// Credentials are handed out by a person, at a screen, on purpose.
/// </summary>
/// <remarks>
/// A sibling of <see cref="AuthController"/> rather than four more actions on
/// it. That controller is already the app's longest and carries the two
/// allow-listed endpoints the wall depends on; these three share nothing with
/// it but a route prefix, and the prefix is what says they belong to the same
/// boundary.
/// </remarks>
[ApiController]
[Route("api/auth/keys")]
[RequireAdmin]
public class ApiKeysController(IAuthService auth, ILogger<ApiKeysController> logger) : ControllerBase
{
    /// <summary>
    /// Every key, newest first, revoked ones included. A revocation is a fact
    /// worth seeing - "that key is gone" is the answer to a question somebody
    /// is asking, and a row that vanished answers nothing.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApiKeyDto>>> ListKeys(CancellationToken ct)
    {
        var keys = await auth.ListApiKeysAsync(ct);
        return keys.Select(ApiKeyDto.From).ToList();
    }

    /// <summary>
    /// Mints a key. The response carries the secret in plaintext - the only
    /// moment it exists outside a hash - so it can be copied into a file once
    /// and never again after the tab is closed. A lost key is replaced by
    /// minting another, not by looking this one up.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ApiKeyMintedDto>> CreateKey([FromBody] CreateApiKeyRequest? request, CancellationToken ct)
    {
        var name = request?.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return BadRequest("a key needs a name - it is what the audit trail will call it");
        if (name.Length > EfApiKey.MaxNameLength)
            return BadRequest($"a key name is at most {EfApiKey.MaxNameLength} characters");

        // Validated rather than stored as sent: an unknown scope is a typo, and
        // a typo silently stored is a key that mysteriously reaches nothing.
        var scopes = request?.Scopes ?? [];
        if (scopes.FirstOrDefault(s => !ApiKeyScopes.IsKnown(s)) is { } unknown)
            return BadRequest($"\"{unknown}\" is not a scope - the scopes are {string.Join(", ", ApiKeyScopes.All)}");

        var created = await auth.CreateApiKeyAsync(name, scopes.Distinct(StringComparer.Ordinal).ToList(), ct);
        if (created is null) return Conflict($"there is already a key called \"{name}\"");

        // A live credential has no business in a cache, anyone's.
        Response.Headers.CacheControl = "no-store";

        logger.LogInformation("API key {KeyId} minted for {Name}", created.Key.Id, created.Key.Name);
        return new ApiKeyMintedDto(ApiKeyDto.From(created.Key), created.Secret);
    }

    /// <summary>
    /// Stops a key working. A POST rather than a DELETE because the row stays:
    /// the audit trail names this key, and "who was Claude in March" is a
    /// question a deleted row cannot answer.
    /// </summary>
    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> RevokeKey(Guid id, CancellationToken ct) =>
        await auth.RevokeApiKeyAsync(id, ct) ? NoContent() : NotFound();
}

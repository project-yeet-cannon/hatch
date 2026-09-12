using Hatch.Api.Common;
using Hatch.Api.Ef;
using Hatch.Api.Services.Auth;
using Hatch.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Hatch.Api.Modules.Hatch;

/// <summary>
/// What Hatch calls whoever is sitting at this machine, so the nav strip can
/// say it back to them.
///
/// <c>204 No Content</c> wherever the wall is up, which is the important answer
/// and is not an error - the same shape <see cref="UtilizationController"/>
/// uses, and for the same reason. On a cluster install a person is a person
/// because they enrolled, their name is already on everything they write, and
/// the strip should draw nothing at all rather than an empty box where
/// something used to be.
/// </summary>
/// <remarks>
/// A read of an identity and not a way to set one. Writing it is
/// <see cref="SettingsController"/>, which writes the same setting this reads -
/// already the first thing <see cref="LocalCaller.PersonNameOf"/> consults, so
/// the two need nothing between them.
/// </remarks>
[ApiController]
[Route("api/hatch/local-person")]
[RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
public class LocalPersonController(
    ICallerIdentity caller,
    ISiteSettingsService settings,
    IOptions<AuthOptions> options) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<LocalPersonDto>> GetLocalPerson(CancellationToken ct)
    {
        // A runner is a program that named itself, not somebody at a keyboard,
        // so it reads as "nobody local" here - the strip this feeds is a
        // browser's, and a browser is never a runner.
        if (await caller.LocalAsync(ct) is not { Kind: ActorKind.Person } person) return NoContent();

        // Asked of the two sources rather than inferred by comparing the name
        // to the default, so an operator who genuinely calls themselves
        // "friend" is not nagged about it for the life of the install.
        var configured = LocalCaller.IsNamed(
            (await settings.GetAsync(ct)).LocalPersonName,
            options.Value.LocalPerson.Name);

        return new LocalPersonDto(person.Name, configured);
    }
}

/// <summary>
/// The local person, as the nav strip needs them.
/// </summary>
/// <param name="Name">What events written from this browser will say.</param>
/// <param name="Configured">
/// Whether anybody actually said so. False means the name is the built-in
/// default and the strip explains what to set - which is the one thing an
/// operator who has just started Hatch for the first time needs told, and the
/// one thing no amount of correct behaviour would tell them.
/// </param>
public record LocalPersonDto(string Name, bool Configured);

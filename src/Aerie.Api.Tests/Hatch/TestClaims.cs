using Aerie.Api.Modules.Hatch;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// The claim rule, for a test that only needs one to exist. Most of these
/// harnesses have nothing to say about leases and want the default TTL; the
/// ones that do say something take it as an argument.
/// </summary>
internal static class TestClaims
{
    /// <summary>The TTL every harness here uses unless it is testing expiry - five minutes, the shipped default.</summary>
    public const int Ttl = 300;

    public static IssueClaims With(int ttlSeconds = Ttl) =>
        new(Options.Create(new HatchOptions { ClaimTtlSeconds = ttlSeconds }));
}

using Hatch.Api.Ef;
using Hatch.Api.Services.Auth;

namespace Hatch.Api.Tests.Auth;

/// <summary>
/// The two secrets the wall deals in. The normalization cases are the ones
/// that matter in a house: a code is read aloud across a room and typed back,
/// so every character a person can plausibly mis-hear has to land on the same
/// row anyway or the fallback that makes QR scanning optional doesn't work.
/// </summary>
public class AuthTokensTests
{
    [Fact]
    public void GrantTokenIsFullEntropyAndUrlSafe()
    {
        var tokens = Enumerable.Range(0, 64).Select(_ => AuthTokens.NewToken()).ToList();

        // 32 bytes of base64url is 43 characters with no padding, and nothing
        // in that alphabet needs escaping in a Set-Cookie header.
        Assert.All(tokens, t => Assert.Equal(43, t.Length));
        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]+$", t));
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void InviteCodeIsEightCrockfordCharacters()
    {
        var codes = Enumerable.Range(0, 64).Select(_ => AuthTokens.NewInviteCode()).ToList();

        Assert.All(codes, c => Assert.Equal(AuthTokens.InviteCodeLength, c.Length));
        // No I, L, O or U - the four that make a code unreadable out loud.
        Assert.All(codes, c => Assert.All(c, ch => Assert.Contains(ch, AuthTokens.InviteAlphabet)));
    }

    [Fact]
    public void HashIsThirtyTwoBytesAndDependsOnTheSecret()
    {
        var hash = AuthTokens.Hash("K3M9P2QT");

        Assert.Equal(AuthHash.Length, hash.Length);
        Assert.Equal(hash, AuthTokens.Hash("K3M9P2QT"));
        Assert.NotEqual(hash, AuthTokens.Hash("K3M9P2QV"));
    }

    [Fact]
    public void FormattedCodeRoundTripsThroughNormalize()
    {
        var code = AuthTokens.NewInviteCode();

        var formatted = AuthTokens.FormatInviteCode(code);

        Assert.StartsWith($"{AuthTokens.InvitePrefix}-", formatted);
        Assert.Equal(code, AuthTokens.NormalizeInviteCode(formatted));
    }

    [Theory]
    // The bare code, which is what a QR deep link carries.
    [InlineData("K3M9P2QT", "K3M9P2QT")]
    // What a paste of the whole displayed code looks like.
    [InlineData("HATCH-K3M9-P2QT", "K3M9P2QT")]
    [InlineData("hatch-k3m9-p2qt", "K3M9P2QT")]
    // Someone typing what they heard: I and L are 1, O is 0. The prefix has to
    // come off before that fold, or "HATCH" becomes "AER1E" and stops matching.
    [InlineData("HATCH-lOOI-K3M9", "1001K3M9")]
    [InlineData("i0l1k3m9", "1011K3M9")]
    // Spaces and underscores are what a phone keyboard adds unasked.
    [InlineData("HATCH K3M9 P2QT", "K3M9P2QT")]
    [InlineData("hatch_k3m9_p2qt", "K3M9P2QT")]
    [InlineData("  HATCH-K3M9-P2QT  ", "K3M9P2QT")]
    public void NormalizeFoldsWhatAPersonPlausiblyTypes(string input, string expected) =>
        Assert.Equal(expected, AuthTokens.NormalizeInviteCode(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Too short, too long - a code is exactly eight characters.
    [InlineData("K3M9P2Q")]
    [InlineData("K3M9P2QTX")]
    // U is not in Crockford's alphabet at all, so it is a typo rather than a fold.
    [InlineData("K3M9P2QU")]
    // The prefix without a code behind it, and a code with the wrong prefix.
    [InlineData("HATCH")]
    [InlineData("HOUSE-K3M9-P2QT")]
    [InlineData("K3M9P2Q!")]
    public void NormalizeRefusesWhatCannotBeACode(string? input) =>
        Assert.Null(AuthTokens.NormalizeInviteCode(input));

    [Fact]
    public void MatchesIsTheOnlyComparatorAndRefusesEverythingButAnExactHash()
    {
        var hash = AuthTokens.Hash("K3M9P2QT");

        Assert.True(AuthTokens.Matches(hash, AuthTokens.Hash("K3M9P2QT")));
        Assert.False(AuthTokens.Matches(hash, AuthTokens.Hash("K3M9P2QV")));
        Assert.False(AuthTokens.Matches(null, hash));

        // A stored value that isn't a SHA-256 can't be equal to one, and
        // FixedTimeEquals throws rather than answers on a length mismatch - so
        // the length check in front of it is load-bearing, not decoration.
        Assert.False(AuthTokens.Matches([1, 2, 3], hash));

        // One bit, which is the case a non-constant-time comparison would
        // answer faster than a wholly different hash.
        var offByOne = (byte[])hash.Clone();
        offByOne[^1] ^= 0x01;
        Assert.False(AuthTokens.Matches(hash, offByOne));
    }
}

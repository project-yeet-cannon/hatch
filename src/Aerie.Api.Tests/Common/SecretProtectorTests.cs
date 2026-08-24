using Aerie.Api.Common;

namespace Aerie.Api.Tests.Common;

/// <summary>
/// docs/plans/cameras.md Phase 11. The property these tests exist to pin is not
/// "XOR round-trips" - that was true before - but that the *format* can change
/// under the callers: a value written by any scheme this build knows reads
/// back, including the untagged ones written before the format existed.
/// </summary>
public class SecretProtectorTests
{
    [Fact]
    public void Protect_then_unprotect_round_trips()
    {
        var stored = SecretProtector.Protect("hunter2");

        Assert.Equal("hunter2", SecretProtector.Unprotect(stored));
    }

    [Fact]
    public void Protect_tags_the_value_with_the_current_scheme()
    {
        // The tag is the whole mechanism, so it is asserted directly rather
        // than only through a round trip that would pass without it.
        Assert.StartsWith("v1:", SecretProtector.Protect("hunter2"), StringComparison.Ordinal);
    }

    [Fact]
    public void Untagged_legacy_values_still_read()
    {
        // What every row in the database looked like before this type existed.
        // If this ever fails, the data migration is no longer a labelling pass
        // and every stored secret in every installation is unreadable.
        var legacy = SecretObfuscator.Obfuscate("legacy-token");

        Assert.Equal("legacy-token", SecretProtector.Unprotect(legacy));
    }

    [Fact]
    public void Retag_relabels_a_legacy_value_without_changing_its_payload()
    {
        var legacy = SecretObfuscator.Obfuscate("legacy-token");

        var retagged = SecretProtector.Retag(legacy);

        // Same bytes, new label - which is why the migration is an UPDATE that
        // prepends three characters rather than a decrypt/re-encrypt pass.
        Assert.Equal($"v1:{legacy}", retagged);
        Assert.Equal("legacy-token", SecretProtector.Unprotect(retagged));
    }

    [Fact]
    public void Retag_leaves_an_already_tagged_value_alone()
    {
        var tagged = SecretProtector.Protect("hunter2");

        // The migration has to be safe to run twice - a re-run, or a row
        // written by the new code before the migration lands.
        Assert.Equal(tagged, SecretProtector.Retag(tagged));
    }

    [Fact]
    public void An_unknown_scheme_reads_as_absent_rather_than_throwing()
    {
        // A row written by a newer Aerie than this one, seen by a rolled-back
        // deployment. Unreadable is the honest answer and it is the same
        // answer callers already handle for a damaged value.
        Assert.Null(SecretProtector.Unprotect("v9:AAAA"));
    }

    [Fact]
    public void A_damaged_payload_reads_as_absent_rather_than_throwing()
    {
        Assert.Null(SecretProtector.Unprotect("v1:not-base64!!"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_stored_reads_as_nothing(string? stored)
    {
        Assert.Null(SecretProtector.Unprotect(stored));
        Assert.False(SecretProtector.HasValue(stored));
    }

    [Fact]
    public void Protecting_nothing_stores_nothing()
    {
        // Rather than a tagged empty payload: "unset" is a state the settings
        // UI and the camera form both have, and tagging it would turn every
        // cleared field into a present value that decodes to nothing.
        Assert.Equal(string.Empty, SecretProtector.Protect(""));
        Assert.Equal(string.Empty, SecretProtector.Protect(null));
    }

    [Fact]
    public void HasValue_does_not_need_to_decode_to_answer()
    {
        // What an admin API asks: has the operator set a password? It must be
        // answerable without revealing one, and without a damaged row
        // reporting "unset" and inviting a silent overwrite.
        Assert.True(SecretProtector.HasValue("v1:not-base64!!"));
        Assert.True(SecretProtector.HasValue(SecretProtector.Protect("hunter2")));
    }

    [Fact]
    public void Round_trips_a_password_full_of_url_metacharacters()
    {
        // Camera passwords go into an RTSP URL, so the characters that break
        // one are exactly the characters worth pinning here.
        const string awkward = "p@ss:w/rd?#&=%[]";

        Assert.Equal(awkward, SecretProtector.Unprotect(SecretProtector.Protect(awkward)));
    }
}

using System.Reflection;
using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Models.Auth;
using Aerie.Api.Modules.Hatch;
using Aerie.Api.Services.Auth;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// Hatch's own two settings, written from Hatch's own page.
///
/// Backed by the real <see cref="SiteSettingsService"/> rather than a stub,
/// because the property worth proving is the one a stub would hide: a write
/// followed by a read on the same request sees the new value, which is what
/// makes the battery reflect a new token without a restart.
/// </summary>
public class SettingsControllerTests
{
    [Fact]
    public async Task WithNothingSet_BothFieldsAreEmpty()
    {
        var settings = await NewFixture().Controller.GetHatchSettings(default);

        Assert.Equal("", settings.ClaudeSubscriptionToken);
        Assert.Equal("", settings.LocalPersonName);
    }

    [Fact]
    public async Task ASavedToken_ReadsBackAsDotsAndNeverAsItself()
    {
        var fixture = NewFixture();

        var settings = await fixture.Controller.PutHatchSettings(new(ClaudeSubscriptionToken: "sk-ant-oat-1", null), default);

        Assert.DoesNotContain("sk-ant-oat-1", settings.ClaudeSubscriptionToken);
        Assert.NotEqual("", settings.ClaudeSubscriptionToken);
    }

    /// <summary>Stored the way every other secret-valued setting is: never in cleartext, even in the row.</summary>
    [Fact]
    public async Task ASavedToken_IsProtectedAtRestAndReachesTheCredential()
    {
        var fixture = NewFixture();

        await fixture.Controller.PutHatchSettings(new(ClaudeSubscriptionToken: "sk-ant-oat-1", null), default);

        var stored = fixture.Db.SiteSettings.Single(s => s.Key == SiteSettingKeys.ClaudeSubscriptionToken).Value;
        Assert.DoesNotContain("sk-ant-oat-1", stored);
        Assert.Equal("sk-ant-oat-1", await fixture.Credential.GetTokenAsync(default));
    }

    /// <summary>The whole of "clearing it removes it": a value sent, not a second verb.</summary>
    [Fact]
    public async Task ClearingTheToken_LeavesItReadingExactlyLikeOneNobodySet()
    {
        var fixture = NewFixture();
        await fixture.Controller.PutHatchSettings(new(ClaudeSubscriptionToken: "sk-ant-oat-1", null), default);

        var settings = await fixture.Controller.PutHatchSettings(new(ClaudeSubscriptionToken: "", null), default);

        Assert.Equal("", settings.ClaudeSubscriptionToken);
        Assert.Null(await fixture.Credential.GetTokenAsync(default));
    }

    /// <summary>A second save replaces rather than appends - there is one row, and it is the one the admin page wrote to.</summary>
    [Fact]
    public async Task SavingAgain_ReplacesTheTokenInPlace()
    {
        var fixture = NewFixture();
        await fixture.Controller.PutHatchSettings(new(ClaudeSubscriptionToken: "first", null), default);

        await fixture.Controller.PutHatchSettings(new(ClaudeSubscriptionToken: "second", null), default);

        Assert.Single(fixture.Db.SiteSettings.Where(s => s.Key == SiteSettingKeys.ClaudeSubscriptionToken));
        Assert.Equal("second", await fixture.Credential.GetTokenAsync(default));
    }

    /// <summary>
    /// Criterion 6, as a test: this is the same site setting the admin page
    /// wrote, under the same key and with the same protection, so a token set
    /// before any of this landed is already set here.
    /// </summary>
    [Fact]
    public async Task ATokenSetBeforeThisPageExisted_ReadsBackOnIt()
    {
        var fixture = NewFixture((SiteSettingKeys.ClaudeSubscriptionToken, SecretProtector.Protect("set-by-the-admin-page")));

        var settings = await fixture.Controller.GetHatchSettings(default);

        Assert.NotEqual("", settings.ClaudeSubscriptionToken);
        Assert.Equal("set-by-the-admin-page", await fixture.Credential.GetTokenAsync(default));
    }

    /// <summary>Not a secret, so it comes back to be edited rather than redacted.</summary>
    [Fact]
    public async Task TheName_ReadsBackAsItself()
    {
        var fixture = NewFixture();

        var settings = await fixture.Controller.PutHatchSettings(new(null, LocalPersonName: "Ada"), default);

        Assert.Equal("Ada", settings.LocalPersonName);
        Assert.Equal("Ada", (await fixture.Settings.GetAsync(default)).LocalPersonName);
    }

    [Fact]
    public async Task ClearingTheName_PutsItBackToNobodyHavingSaid()
    {
        var fixture = NewFixture();
        await fixture.Controller.PutHatchSettings(new(null, LocalPersonName: "Ada"), default);

        var settings = await fixture.Controller.PutHatchSettings(new(null, LocalPersonName: ""), default);

        Assert.Equal("", settings.LocalPersonName);
        Assert.Null((await fixture.Settings.GetAsync(default)).LocalPersonName);
    }

    /// <summary>The bulk-edit convention: a field left out is a field left alone, so one form's Save cannot wipe the other's.</summary>
    [Fact]
    public async Task AnOmittedField_IsLeftAlone()
    {
        var fixture = NewFixture();
        await fixture.Controller.PutHatchSettings(new(ClaudeSubscriptionToken: "sk-ant-oat-1", LocalPersonName: "Ada"), default);

        var settings = await fixture.Controller.PutHatchSettings(new(null, LocalPersonName: "Grace"), default);

        Assert.Equal("Grace", settings.LocalPersonName);
        Assert.Equal("sk-ant-oat-1", await fixture.Credential.GetTokenAsync(default));
    }

    [Fact]
    public async Task ARequestNamingNeitherField_WritesNothing()
    {
        var fixture = NewFixture();

        await fixture.Controller.PutHatchSettings(new(null, null), default);

        Assert.Empty(fixture.Db.SiteSettings);
    }

    /// <summary>
    /// Wherever the wall is up the name comes from the grant, so the page has
    /// no field to draw - the route still answers, and it answers that.
    /// </summary>
    [Fact]
    public async Task WithTheWallUp_TheNameDoesNotApply()
    {
        var fixture = NewFixture(authEnabled: true);

        Assert.False((await fixture.Controller.GetHatchSettings(default)).LocalPersonNameApplies);
    }

    [Fact]
    public async Task WithTheWallDown_TheNameApplies()
    {
        Assert.True((await NewFixture().Controller.GetHatchSettings(default)).LocalPersonNameApplies);
    }

    // ---- The token itself, for the container runner ----

    /// <summary>
    /// The one endpoint in Aerie that hands a live secret back out, and what
    /// makes it usable: a container holding nothing but git and the hatch
    /// binary reverses the wrapper locally, with no second round trip and no
    /// openssl.
    /// </summary>
    [Fact]
    public async Task TheTokenRoute_AnswersItWrappedTheWayTheStoreWrapsIt()
    {
        var fixture = NewFixture();
        await fixture.Controller.PutHatchSettings(new(ClaudeSubscriptionToken: "sk-ant-oat-1", null), default);

        var answer = await fixture.Controller.GetClaudeToken(default);

        var dto = Assert.IsType<ClaudeTokenDto>(answer.Value);
        Assert.DoesNotContain("sk-ant-oat-1", dto.ProtectedToken);
        Assert.Equal("sk-ant-oat-1", SecretProtector.Unprotect(dto.ProtectedToken));
    }

    /// <summary>
    /// Nothing set is not a failure - it is the state a friend is in between
    /// starting the stack and pasting a token, and the entrypoint waits it out
    /// (criterion 4). So the same 204 the battery answers with, and not a 404.
    /// </summary>
    [Fact]
    public async Task WithNoTokenSaved_TheTokenRouteAnswersNoContent()
    {
        var answer = await NewFixture().Controller.GetClaudeToken(default);

        Assert.IsType<NoContentResult>(answer.Result);
    }

    /// <summary>A token cleared reads exactly like one nobody set, here too.</summary>
    [Fact]
    public async Task WithTheTokenCleared_TheTokenRouteAnswersNoContent()
    {
        var fixture = NewFixture();
        await fixture.Controller.PutHatchSettings(new(ClaudeSubscriptionToken: "sk-ant-oat-1", null), default);
        await fixture.Controller.PutHatchSettings(new(ClaudeSubscriptionToken: "", null), default);

        Assert.IsType<NoContentResult>((await fixture.Controller.GetClaudeToken(default)).Result);
    }

    /// <summary>
    /// The second gate, and the one the attribute cannot express: wherever the
    /// wall is up nobody gets the token - not a scoped key, not an
    /// administrator at a browser. A route this far inside the filter has
    /// already been told the caller is allowed, so this refusal is the action's
    /// own and it is unconditional.
    /// </summary>
    [Fact]
    public async Task WithTheWallUp_TheTokenRouteRefusesEverybody()
    {
        var fixture = NewFixture(authEnabled: true);
        await fixture.Controller.PutHatchSettings(new(ClaudeSubscriptionToken: "sk-ant-oat-1", null), default);

        var answer = await fixture.Controller.GetClaudeToken(default);

        var refusal = Assert.IsType<ObjectResult>(answer.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, refusal.StatusCode);
        Assert.IsType<AuthErrorDto>(refusal.Value);
        Assert.DoesNotContain("sk-ant-oat-1", System.Text.Json.JsonSerializer.Serialize(refusal.Value));
    }

    /// <summary>
    /// The split itself, asserted rather than described: the token route takes
    /// a Hatch-scoped key because a keyless runner is its ordinary caller, and
    /// the two routes beside it still take a person and nothing else. Read off
    /// the attributes because that is where the decision lives - and because
    /// RequireAdminAttribute is AllowMultiple = false, so an attribute added at
    /// the class level later would silently replace all three.
    /// </summary>
    [Fact]
    public void TheTokenRouteTakesAKeyAndTheOtherTwoDoNot()
    {
        Assert.Equal(ApiKeyScopes.Hatch, Guard(nameof(SettingsController.GetClaudeToken)).AcceptScope);
        Assert.Null(Guard(nameof(SettingsController.GetHatchSettings)).AcceptScope);
        Assert.Null(Guard(nameof(SettingsController.PutHatchSettings)).AcceptScope);

        static RequireAdminAttribute Guard(string action) =>
            typeof(SettingsController).GetMethod(action)!.GetCustomAttribute<RequireAdminAttribute>()
            ?? throw new Xunit.Sdk.XunitException($"{action} carries no [RequireAdmin] of its own.");
    }

    private static Fixture NewFixture(params (string Key, string Value)[] seed) => NewFixture(false, seed);

    private static Fixture NewFixture(bool authEnabled, params (string Key, string Value)[] seed)
    {
        var options = new DbContextOptionsBuilder<AerieContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new AerieContext(options);
        db.SiteSettings.AddRange(seed.Select(s => new EfSiteSetting { Key = s.Key, Value = s.Value }));
        db.SaveChanges();

        // A FakeTimeProvider that never advances, so the snapshot's TTL never
        // expires on its own: anything these tests see refreshed was refreshed
        // by the controller's own Invalidate, which is the point.
        var settings = new SiteSettingsService(new TestDbContextFactory(options), new FakeTimeProvider());

        var credential = new SiteSettingClaudeCredential(settings);
        var controller = new SettingsController(
            db, settings, credential, Options.Create(new AuthOptions { Enabled = authEnabled }));

        return new Fixture(controller, db, settings, credential);
    }

    private sealed record Fixture(
        SettingsController Controller,
        AerieContext Db,
        ISiteSettingsService Settings,
        IClaudeCredential Credential);

    private sealed class TestDbContextFactory(DbContextOptions<AerieContext> options) : IDbContextFactory<AerieContext>
    {
        public AerieContext CreateDbContext() => new(options);
        public Task<AerieContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new AerieContext(options));
    }
}

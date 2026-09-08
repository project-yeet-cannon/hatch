using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Modules.Hatch;
using Aerie.Api.Services.Auth;
using Aerie.Api.Services.DeviceMapping;
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

        var controller = new SettingsController(
            db, settings, Options.Create(new AuthOptions { Enabled = authEnabled }));

        return new Fixture(controller, db, settings, new SiteSettingClaudeCredential(settings));
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

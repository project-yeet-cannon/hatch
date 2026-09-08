using Aerie.Api.Ef;
using Aerie.Api.Modules.Hatch;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// What the nav strip is told about who is sitting here - and, on a cluster
/// install, that it is told nothing at all rather than an empty box.
/// </summary>
public class LocalPersonControllerTests
{
    [Fact]
    public async Task WithTheWallUp_ThereIsNothingToDraw()
    {
        var result = await NewController(local: null).GetLocalPerson(default);

        Assert.IsType<NoContentResult>(result.Result);
    }

    [Fact]
    public async Task WithANameConfigured_ItIsAnsweredAndSaidToBeConfigured()
    {
        var controller = NewController(
            local: new Actor(ActorKind.Person, LocalCaller.PersonId, "Ada"),
            configuredName: "Ada");

        var person = Value(await controller.GetLocalPerson(default));

        Assert.Equal("Ada", person.Name);
        Assert.True(person.Configured);
    }

    /// <summary>
    /// The one thing an operator who has just started Aerie for the first time
    /// needs told, and the one thing no amount of correct behaviour would tell
    /// them.
    /// </summary>
    [Fact]
    public async Task WithNothingConfiguredAnywhere_TheNameIsTheDefaultAndItSaysSo()
    {
        var controller = NewController(
            local: new Actor(ActorKind.Person, LocalCaller.PersonId, LocalCaller.DefaultName));

        var person = Value(await controller.GetLocalPerson(default));

        Assert.Equal(LocalCaller.DefaultName, person.Name);
        Assert.False(person.Configured);
    }

    /// <summary>The setting is read too, which is what makes AERIE-936 a page rather than a restart.</summary>
    [Fact]
    public async Task ANameFromTheSettingsTable_CountsAsConfigured()
    {
        var controller = NewController(
            local: new Actor(ActorKind.Person, LocalCaller.PersonId, "Grace"),
            settingName: "Grace");

        Assert.True(Value(await controller.GetLocalPerson(default)).Configured);
    }

    /// <summary>A runner is a program that named itself, and the strip this feeds is a browser's.</summary>
    [Fact]
    public async Task ARunner_IsNobodyToDraw()
    {
        var runner = new Actor(ActorKind.Key, LocalCaller.RunnerIdFor("host:/src"), "host:/src");

        Assert.IsType<NoContentResult>((await NewController(runner).GetLocalPerson(default)).Result);
    }

    private static LocalPersonController NewController(
        Actor? local, string? configuredName = null, string? settingName = null) =>
        new(
            new StubLocalCaller(local),
            new StubSiteSettings(localPersonName: settingName),
            Options.Create(new AuthOptions { LocalPerson = { Name = configuredName ?? "" } }));

    private static T Value<T>(ActionResult<T> result) =>
        result.Value ?? throw new InvalidOperationException("expected a value");

    /// <summary>A caller that is only ever the third lane, which is every request this route answers.</summary>
    private sealed class StubLocalCaller(Actor? local) : ICallerIdentity
    {
        public Task<EfAuthGrant?> GrantAsync(CancellationToken ct) => Task.FromResult<EfAuthGrant?>(null);

        public Task<Guid?> PersonIdAsync(CancellationToken ct) => Task.FromResult<Guid?>(null);

        public Task<EfPerson?> PersonAsync(CancellationToken ct) => Task.FromResult<EfPerson?>(null);

        public Task<EfApiKey?> ApiKeyAsync(CancellationToken ct) => Task.FromResult<EfApiKey?>(null);

        public Task<Actor?> LocalAsync(CancellationToken ct) => Task.FromResult(local);

        public Task<bool> IsProgramAsync(CancellationToken ct) =>
            Task.FromResult(local is { Kind: ActorKind.Key });

        public Task<string> ActorNameAsync(CancellationToken ct) =>
            Task.FromResult(local?.Name ?? CallerIdentity.Unattributed);
    }
}

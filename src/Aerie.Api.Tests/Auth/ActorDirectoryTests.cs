using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Auth;

/// <summary>
/// Who is out there, once local mode has somebody in it.
///
/// The directory is where "Assign to me" is actually decided:
/// AssigneeController answers <c>me</c> by looking the caller up <em>inside</em>
/// LiveAsync, so a local person who was not in the list would be offered no
/// press however MeAsync answered. It is also the one resolver every reader
/// goes through, which is what makes the board, the peek and the plan one
/// change rather than three.
/// </summary>
public class ActorDirectoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static readonly Actor LocalPerson =
        new(ActorKind.Person, LocalCaller.PersonId, "Ada");

    [Fact]
    public async Task TheLocalPerson_IsWhoIAmWhenNothingElseAnswered()
    {
        var directory = NewDirectory(local: LocalPerson);

        Assert.Equal(LocalPerson, await directory.MeAsync(default));
    }

    /// <summary>
    /// First, ahead of the A→Z. "You" is not one name among many on a picker:
    /// it is the one press that is always the same press.
    /// </summary>
    [Fact]
    public async Task TheLocalPerson_LeadsTheDirectory()
    {
        var db = NewDb();
        db.Add(NewPerson("Alan"));
        db.Add(NewPerson("Zoe"));
        await db.SaveChangesAsync();

        var live = await NewDirectory(db, LocalPerson).LiveAsync(default);

        Assert.Equal(["Ada", "Alan", "Zoe"], live.Select(a => a.Name));
    }

    /// <summary>
    /// The whole point of the fixed id: the name is only what is drawn, so
    /// renaming yourself leaves every issue you were assigned still assigned.
    /// </summary>
    [Fact]
    public async Task RenamingYourself_StillResolvesToTheSameActor()
    {
        var renamed = LocalPerson with { Name = "Ada Lovelace" };

        var resolved = await NewDirectory(local: renamed)
            .ResolveAsync(ActorKind.Person, LocalCaller.PersonId, default);

        Assert.Equal("Ada Lovelace", resolved?.Name);
    }

    /// <summary>
    /// A runner is transient and self-named, so nothing may be assigned to one -
    /// and an id that means nothing tomorrow resolving to nobody today is the
    /// liveness rule reading correctly rather than a gap.
    /// </summary>
    [Fact]
    public async Task ARunner_IsNotInTheDirectoryAndNothingResolvesToIt()
    {
        var runner = new Actor(ActorKind.Key, LocalCaller.RunnerIdFor("host:/src"), "host:/src");
        var directory = NewDirectory(local: runner);

        Assert.Empty(await directory.LiveAsync(default));
        Assert.Null(await directory.ResolveAsync(ActorKind.Key, runner.Id, default));

        // It is still who is calling, though - that is what puts its name in
        // the trail.
        Assert.Equal(runner, await directory.MeAsync(default));
    }

    /// <summary>With a wall up there is no local caller, and the directory is what it always was.</summary>
    [Fact]
    public async Task WithNoLocalCaller_NothingIsPrepended()
    {
        var db = NewDb();
        db.Add(NewPerson("Alan"));
        await db.SaveChangesAsync();

        var directory = NewDirectory(db);

        Assert.Equal(["Alan"], (await directory.LiveAsync(default)).Select(a => a.Name));
        Assert.Null(await directory.MeAsync(default));
    }

    private static AerieContext NewDb() => new(
        new DbContextOptionsBuilder<AerieContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static EfPerson NewPerson(string name) =>
        new() { Id = Guid.NewGuid(), Name = name, CreatedAt = Now, UpdatedAt = Now };

    private static ActorDirectory NewDirectory(AerieContext? db = null, Actor? local = null) =>
        new(db ?? NewDb(), new StubLocalCaller(local), new FakeTimeProvider(Now));

    /// <summary>A caller that is only ever the third lane - which is every request in local mode.</summary>
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

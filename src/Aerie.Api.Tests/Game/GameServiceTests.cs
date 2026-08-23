using Aerie.Api.Modules.Game;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Game;

/// <summary>
/// The version chain: what a turn appends, what a crash marks, and what going
/// back lands on. Against EF Core InMemory, with the model itself faked - none
/// of the behaviour worth testing here is about what the model wrote, only
/// about which version is live afterwards.
/// </summary>
public class GameServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ANewWorld_StartsOnTheSeed()
    {
        await using var db = NewContext();
        var world = await NewService(db).CreateWorldAsync(new WorldWriteRequest("Ball game", "\U0001F3AE"), default);

        Assert.Equal("Ball game", world.World.Name);
        Assert.Equal(GameVersionKind.Seed, world.Current!.Kind);
        Assert.Equal(1, world.Current.Ordinal);
        Assert.Equal(GameService.SeedCode, world.Code);
        Assert.Empty(world.Records);

        // The pointer has to survive the second write that sets it - a world
        // that opens on nothing is the one failure nobody could work around.
        var stored = await db.Worlds.SingleAsync();
        Assert.Equal(world.Current.Id, stored.CurrentVersionId);
    }

    [Fact]
    public async Task ATurn_BecomesTheLiveVersion()
    {
        await using var db = NewContext();
        var author = new FakeAuthor("defineGame({ setup(w) { w.sky('#fff'); } });", "The sky is white!", "Try to jump!");
        var service = NewService(db, author);
        var created = await service.CreateWorldAsync(new WorldWriteRequest("Ball game", null), default);

        var world = await service.TakeTurnAsync(created.World.Id, new TurnRequest("make it white", GameModelChoice.Quick), default);

        Assert.NotNull(world);
        Assert.Equal(GameVersionKind.Turn, world.Current!.Kind);
        Assert.Equal(2, world.Current.Ordinal);
        Assert.Equal("make it white", world.Current.Prompt);
        Assert.Equal("The sky is white!", world.Current.Summary);
        Assert.Equal("Try to jump!", world.Current.Extra);
        Assert.Equal(author.Code, world.Code);

        // The seed is not shown to the model: an empty sky is not a starting
        // point worth spending tokens describing.
        Assert.Null(author.LastRequest!.CurrentCode);
        Assert.Equal("make it white", author.LastRequest.Instruction);
    }

    [Fact]
    public async Task ASecondTurn_SeesTheFirstOnesCodeAndPrompt()
    {
        await using var db = NewContext();
        var author = new FakeAuthor("defineGame({ setup(w) {} });");
        var service = NewService(db, author);
        var created = await service.CreateWorldAsync(new WorldWriteRequest("Ball game", null), default);

        await service.TakeTurnAsync(created.World.Id, new TurnRequest("i am a red ball", null), default);
        author.Code = "defineGame({ setup(w) { w.hill(0, 100, 900, 500); } });";
        await service.TakeTurnAsync(created.World.Id, new TurnRequest("i want to roll down a hill", null), default);

        Assert.Equal("defineGame({ setup(w) {} });", author.LastRequest!.CurrentCode);
        Assert.Equal(["i am a red ball"], author.LastRequest.RecentPrompts);
    }

    [Fact]
    public async Task ACrash_MarksTheVersion_AndTheRepairBecomesLive()
    {
        await using var db = NewContext();
        var author = new FakeAuthor("defineGame({ setup(w) { boom(); } });");
        var service = NewService(db, author);
        var created = await service.CreateWorldAsync(new WorldWriteRequest("Ball game", null), default);
        var broken = await service.TakeTurnAsync(created.World.Id, new TurnRequest("break it", null), default);

        author.Code = "defineGame({ setup(w) {} });";
        var repaired = await service.RepairAsync(
            created.World.Id,
            new BreakageReport(broken!.Current!.Id, "setup", "ReferenceError: boom is not defined", "at setup"),
            default);

        Assert.Equal(GameVersionKind.Repair, repaired!.Current!.Kind);
        Assert.Equal(author.Code, repaired.Code);
        Assert.True(author.LastRequest!.IsRepair);
        Assert.Contains("boom is not defined", author.LastRequest.Instruction);

        var brokenRow = await db.Versions.SingleAsync(v => v.Id == broken.Current.Id);
        Assert.NotNull(brokenRow.BrokenAt);
        Assert.Contains("[setup]", brokenRow.BrokenError);
    }

    [Fact]
    public async Task ACrashReportedAgainstAVersionThatIsNoLongerLive_ChangesNothing()
    {
        await using var db = NewContext();
        var author = new FakeAuthor("defineGame({ setup(w) {} });");
        var service = NewService(db, author);
        var created = await service.CreateWorldAsync(new WorldWriteRequest("Ball game", null), default);
        var first = await service.TakeTurnAsync(created.World.Id, new TurnRequest("one", null), default);
        var second = await service.TakeTurnAsync(created.World.Id, new TurnRequest("two", null), default);

        // A frame left open in another tab, still running the older version.
        var world = await service.RepairAsync(
            created.World.Id,
            new BreakageReport(first!.Current!.Id, "update", "TypeError", null),
            default);

        Assert.Equal(second!.Current!.Id, world!.Current!.Id);
        Assert.Equal(3, await db.Versions.CountAsync());
        Assert.Null((await db.Versions.SingleAsync(v => v.Id == first.Current.Id)).BrokenAt);
    }

    [Fact]
    public async Task GoingBack_SkipsVersionsThatBroke()
    {
        await using var db = NewContext();
        var author = new FakeAuthor("defineGame({ setup(w) { /* good */ } });");
        var service = NewService(db, author);
        var created = await service.CreateWorldAsync(new WorldWriteRequest("Ball game", null), default);
        var good = await service.TakeTurnAsync(created.World.Id, new TurnRequest("the good one", null), default);

        author.Code = "defineGame({ setup(w) { boom(); } });";
        var bad = await service.TakeTurnAsync(created.World.Id, new TurnRequest("the bad one", null), default);
        await service.ReportBreakageAsync(created.World.Id, new BreakageReport(bad!.Current!.Id, "setup", "boom", null), default);

        var world = await service.UndoAsync(created.World.Id, default);

        Assert.Equal(GameVersionKind.Revert, world!.Current!.Kind);
        Assert.Equal(good!.Code, world.Code);
        // Undo is a new version rather than a moved pointer, so undoing an undo
        // is just another step back.
        Assert.Equal(4, world.Current.Ordinal);
    }

    [Fact]
    public async Task GoingBackFromTheSeed_SaysSoRatherThanFailing()
    {
        await using var db = NewContext();
        var service = NewService(db);
        var created = await service.CreateWorldAsync(new WorldWriteRequest("Ball game", null), default);

        var error = await Assert.ThrowsAsync<GameAuthorException>(() => service.UndoAsync(created.World.Id, default));
        Assert.Contains("nothing before it", error.Message);
    }

    [Fact]
    public async Task RevertingToABrokenVersion_IsRefused()
    {
        await using var db = NewContext();
        var author = new FakeAuthor("defineGame({ setup(w) { boom(); } });");
        var service = NewService(db, author);
        var created = await service.CreateWorldAsync(new WorldWriteRequest("Ball game", null), default);
        var bad = await service.TakeTurnAsync(created.World.Id, new TurnRequest("the bad one", null), default);
        await service.ReportBreakageAsync(created.World.Id, new BreakageReport(bad!.Current!.Id, "setup", "boom", null), default);
        await service.UndoAsync(created.World.Id, default);

        await Assert.ThrowsAsync<GameAuthorException>(
            () => service.RevertAsync(created.World.Id, bad.Current.Id, default));
    }

    [Fact]
    public async Task ATurnAfterACrash_BuildsOnTheLastVersionThatWorked()
    {
        await using var db = NewContext();
        var author = new FakeAuthor("defineGame({ setup(w) { w.sky('#0f0'); } });");
        var service = NewService(db, author);
        var created = await service.CreateWorldAsync(new WorldWriteRequest("Ball game", null), default);
        var good = await service.TakeTurnAsync(created.World.Id, new TurnRequest("green sky", null), default);

        author.Code = "defineGame({ setup(w) { boom(); } });";
        var bad = await service.TakeTurnAsync(created.World.Id, new TurnRequest("a puppy", null), default);
        await service.ReportBreakageAsync(created.World.Id, new BreakageReport(bad!.Current!.Id, "setup", "boom", null), default);

        author.Code = "defineGame({ setup(w) {} });";
        await service.TakeTurnAsync(created.World.Id, new TurnRequest("a kitten", null), default);

        // Handing the model code that throws is how one bad turn becomes every
        // following turn.
        Assert.Equal(good!.Code, author.LastRequest!.CurrentCode);
    }

    [Fact]
    public async Task Records_RoundTrip_AndSurviveABadRow()
    {
        await using var db = NewContext();
        var service = NewService(db);
        var created = await service.CreateWorldAsync(new WorldWriteRequest("Ball game", null), default);

        var saved = await service.SaveRecordsAsync(
            created.World.Id,
            new RecordsWriteRequest([new GameRecordDto("longest-jump", "Longest jump", 18.4, "m", false)]),
            default);

        Assert.True(saved);
        var world = await service.GetWorldAsync(created.World.Id, default);
        var record = Assert.Single(world!.Records);
        Assert.Equal("Longest jump", record.Label);
        Assert.Equal(18.4, record.Value);

        // A record is a nice-to-have; an unopenable game is the end of the
        // afternoon.
        (await db.Worlds.SingleAsync()).RecordsJson = "{ not json";
        await db.SaveChangesAsync();
        Assert.Empty((await service.GetWorldAsync(created.World.Id, default))!.Records);
    }

    [Fact]
    public async Task OpeningTheApp_LandsOnTheGamePlayedLast()
    {
        await using var db = NewContext();
        var service = NewService(db);

        // Nothing has ever been played: one gets made rather than showing an
        // empty list.
        var first = await service.GetOrCreateCurrentWorldAsync(default);
        Assert.NotNull(first.Current);

        var time = new FakeTimeProvider(Now.AddHours(1));
        var later = NewService(db, time: time);
        var second = await later.CreateWorldAsync(new WorldWriteRequest("Something else", null), default);

        Assert.Equal(second.World.Id, (await later.GetOrCreateCurrentWorldAsync(default)).World.Id);
    }

    // ---- helpers

    /// <summary>
    /// Stands in for the model. Returns whatever <see cref="Code"/> is set to
    /// and remembers what it was asked, which is the half of a turn this suite
    /// is actually about.
    /// </summary>
    private sealed class FakeAuthor(string code, string summary = "Done!", string? extra = null) : IGameAuthor
    {
        public string Code { get; set; } = code;
        public GameAuthorRequest? LastRequest { get; private set; }

        public Task<GameCapabilityDto> GetCapabilityAsync(CancellationToken ct) =>
            Task.FromResult(new GameCapabilityDto(true, null));

        public Task<AuthoredGame> WriteAsync(GameAuthorRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new AuthoredGame(Code, summary, extra, "claude-sonnet-5", 1200, 800, 1000, 4200));
        }
    }

    private static GameContext NewContext() =>
        new(new DbContextOptionsBuilder<GameContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static GameService NewService(GameContext db, IGameAuthor? author = null, TimeProvider? time = null) =>
        new(db, author ?? new FakeAuthor("defineGame({ setup(w) {} });"), time ?? new FakeTimeProvider(Now));
}

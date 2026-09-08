using Aerie.Api.Ef;
using Aerie.Api.Modules.Quill;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Quill;

/// <summary>
/// Quill's endpoints, and mostly one property of them: a note is reachable by
/// its owner and by nobody else, and every way of not being its owner produces
/// the same 404.
///
/// That is worth this many tests because it is the first authorization decision
/// in Aerie that reads a person, and because the failure mode is silent - a
/// missing <c>PersonId</c> clause on one query does not break anything a person
/// would notice, it just serves someone else's notes.
/// </summary>
public class QuillControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);

    // ---- The wall around a note ----

    [Fact]
    public async Task Notes_AreOnlyEverThisPersonsNotes()
    {
        var h = await NewHarnessAsync();
        await h.WriteAsync(h.Someone, "Hers", "the good coffee shop");
        await h.WriteAsync(h.Person, "Mine", "the other one");

        var notes = Value(await h.Controller.GetNotes(default));

        Assert.Equal(["Mine"], notes.Select(n => n.Title));
    }

    /// <summary>
    /// A 404 rather than a 403, deliberately. A 403 confirms that the id names a
    /// real note belonging to a real person, which is exactly the fact a private
    /// note has to keep.
    /// </summary>
    [Fact]
    public async Task SomeoneElsesNote_IsIndistinguishableFromANoteThatDoesNotExist()
    {
        var h = await NewHarnessAsync();
        var hers = await h.WriteAsync(h.Someone, "Hers", "the good coffee shop");

        Assert.IsType<NotFoundResult>((await h.Controller.GetNote(hers.Id, default)).Result);
        Assert.IsType<NotFoundResult>((await h.Controller.GetNote(Guid.NewGuid(), default)).Result);
    }

    [Fact]
    public async Task SomeoneElsesNote_CannotBeEditedOrDeleted()
    {
        var h = await NewHarnessAsync();
        var hers = await h.WriteAsync(h.Someone, "Hers", "the good coffee shop");

        Assert.IsType<NotFoundResult>((await h.Controller.UpdateNote(hers.Id, new NoteWriteRequest("Mine now", "ha"), default)).Result);
        Assert.IsType<NotFoundResult>(await h.Controller.DeleteNote(hers.Id, default));

        var untouched = await h.Db.Notes.AsNoTracking().SingleAsync(n => n.Id == hers.Id);
        Assert.Equal("Hers", untouched.Title);
    }

    /// <summary>
    /// A device nobody has claimed gets the same empty 404 everywhere, including
    /// on the list. The shell already declines to show it the app; the API not
    /// agreeing would be the one place a person-less device could learn that
    /// Quill exists at all.
    /// </summary>
    [Fact]
    public async Task WithNoPerson_EveryEndpointIsABlank404()
    {
        var h = await NewHarnessAsync();
        var mine = await h.WriteAsync(h.Person, "Mine", "still mine");
        h.Caller.PersonId = null;

        Assert.IsType<NotFoundResult>((await h.Controller.GetNotes(default)).Result);
        Assert.IsType<NotFoundResult>((await h.Controller.GetNote(mine.Id, default)).Result);
        Assert.IsType<NotFoundResult>((await h.Controller.CreateNote(new NoteWriteRequest(null, "hello"), default)).Result);
        Assert.IsType<NotFoundResult>((await h.Controller.UpdateNote(mine.Id, new NoteWriteRequest(null, "hello"), default)).Result);
        Assert.IsType<NotFoundResult>(await h.Controller.DeleteNote(mine.Id, default));
    }

    /// <summary>
    /// The refusal comes before the validation. An unclaimed device posting
    /// something malformed must not learn from a 400 that it would have been a
    /// 404 - two different refusals is the leak the single 404 exists to close.
    /// </summary>
    [Fact]
    public async Task WithNoPerson_ABadRequestIsStillJustA404()
    {
        var h = await NewHarnessAsync();
        h.Caller.PersonId = null;

        var result = await h.Controller.CreateNote(new NoteWriteRequest(new string('x', QuillNote.MaxTitleLength + 1), null), default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- Writing ----

    [Fact]
    public async Task CreateNote_StartsWithJustABody()
    {
        var h = await NewHarnessAsync();

        var created = Created(await h.Controller.CreateNote(new NoteWriteRequest(null, "milk, and the thing about the fence"), default));

        // Untitled is a state, not a failure: you start typing the body and may
        // never name it.
        Assert.Equal("", created.Title);
        Assert.Equal("milk, and the thing about the fence", created.Body);
        Assert.Equal(Now, created.CreatedAt);
        Assert.Equal(Now, created.UpdatedAt);
    }

    [Fact]
    public async Task CreateNote_TrimsTheTitleAndLeavesTheBodyExactlyAsTyped()
    {
        var h = await NewHarnessAsync();

        var created = Created(await h.Controller.CreateNote(new NoteWriteRequest("  Fence  ", "\n  two posts\n\n"), default));

        Assert.Equal("Fence", created.Title);
        // Leading blank lines are something a person typed into their own note.
        Assert.Equal("\n  two posts\n\n", created.Body);
    }

    [Fact]
    public async Task CreateNote_RefusesANoteWithNothingInIt()
    {
        var h = await NewHarnessAsync();

        Assert.IsType<BadRequestObjectResult>((await h.Controller.CreateNote(new NoteWriteRequest(null, null), default)).Result);
        Assert.IsType<BadRequestObjectResult>((await h.Controller.CreateNote(new NoteWriteRequest("  ", "\n \t "), default)).Result);
        Assert.Empty(await h.Db.Notes.ToListAsync());
    }

    [Fact]
    public async Task CreateNote_RefusesTextTooLongForTheColumnsToBeHonestAbout()
    {
        var h = await NewHarnessAsync();

        Assert.IsType<BadRequestObjectResult>(
            (await h.Controller.CreateNote(new NoteWriteRequest(new string('x', QuillNote.MaxTitleLength + 1), "hi"), default)).Result);
        Assert.IsType<BadRequestObjectResult>(
            (await h.Controller.CreateNote(new NoteWriteRequest(null, new string('x', QuillNote.MaxBodyLength + 1)), default)).Result);
    }

    [Fact]
    public async Task UpdateNote_OverwritesBothFieldsAndMovesUpdatedAt()
    {
        var h = await NewHarnessAsync();
        var note = await h.WriteAsync(h.Person, "Fence", "two posts");
        h.Time.Advance(TimeSpan.FromMinutes(5));

        var updated = Value(await h.Controller.UpdateNote(note.Id, new NoteWriteRequest("Fence", "three posts"), default));

        Assert.Equal("three posts", updated.Body);
        Assert.Equal(Now, updated.CreatedAt);
        Assert.Equal(Now.AddMinutes(5), updated.UpdatedAt);
    }

    /// <summary>
    /// Blanking a note leaves an empty note. The editor saves as you type, so a
    /// server that deleted the row the moment someone selected all and started
    /// retyping would be a server that eats notes.
    /// </summary>
    [Fact]
    public async Task UpdateNote_MayBlankTheNoteWithoutDeletingIt()
    {
        var h = await NewHarnessAsync();
        var note = await h.WriteAsync(h.Person, "Fence", "two posts");

        var updated = Value(await h.Controller.UpdateNote(note.Id, new NoteWriteRequest("", ""), default));

        Assert.Equal("", updated.Title);
        Assert.Equal("", updated.Body);
        Assert.Single(await h.Db.Notes.ToListAsync());
    }

    [Fact]
    public async Task DeleteNote_TakesItAndNothingElse()
    {
        var h = await NewHarnessAsync();
        var first = await h.WriteAsync(h.Person, "Fence", "two posts");
        await h.WriteAsync(h.Person, "Groceries", "milk");

        Assert.IsType<NoContentResult>(await h.Controller.DeleteNote(first.Id, default));

        var left = await h.Db.Notes.AsNoTracking().SingleAsync();
        Assert.Equal("Groceries", left.Title);
    }

    // ---- Reading ----

    /// <summary>
    /// Most recently edited first: a notes list answers "what was I working on",
    /// not "what did I start".
    /// </summary>
    [Fact]
    public async Task GetNotes_PutsTheMostRecentlyEditedFirst()
    {
        var h = await NewHarnessAsync();
        var first = await h.WriteAsync(h.Person, "First", "a");
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.WriteAsync(h.Person, "Second", "b");
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.Controller.UpdateNote(first.Id, new NoteWriteRequest("First", "a, revised"), default);

        var notes = Value(await h.Controller.GetNotes(default));

        Assert.Equal(["First", "Second"], notes.Select(n => n.Title));
    }

    /// <summary>
    /// The list carries whole notes, bodies and all. That is what makes the
    /// shell's offline mirror complete by construction: a mirror filled from
    /// summaries could only offer the notes someone had happened to open, and
    /// the one written on Monday and not re-opened is exactly the one wanted on
    /// Thursday.
    /// </summary>
    [Fact]
    public async Task GetNotes_CarriesWholeNotes()
    {
        var h = await NewHarnessAsync();
        await h.WriteAsync(h.Person, "Fence", "two posts, and the gate latch");

        var note = Assert.Single(Value(await h.Controller.GetNotes(default)));

        Assert.Equal("two posts, and the gate latch", note.Body);
    }

    /// <summary>
    /// End to end through the converter: what went in comes back out, which is
    /// the half of protection-at-rest that QuillContextTests cannot see.
    /// </summary>
    [Fact]
    public async Task ANoteReadsBackExactlyAsItWasWritten()
    {
        var h = await NewHarnessAsync();
        const string body = "ünïcode, emoji 🪶, and a\ttab";

        var created = Created(await h.Controller.CreateNote(new NoteWriteRequest("Odd characters", body), default));
        var read = Value(await h.Controller.GetNote(created.Id, default));

        Assert.Equal(body, read.Body);
        Assert.Equal("Odd characters", read.Title);
    }

    private static async Task<Harness> NewHarnessAsync()
    {
        var db = new QuillContext(
            new DbContextOptionsBuilder<QuillContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var time = new FakeTimeProvider(Now);
        var person = Guid.NewGuid();
        var caller = new StubCallerIdentity { PersonId = person };

        await Task.CompletedTask;
        return new Harness(new QuillController(db, caller, time), db, caller, person, Guid.NewGuid(), time);
    }

    private static T Value<T>(ActionResult<T> result) =>
        result.Value ?? throw new InvalidOperationException($"expected a value, got {result.Result?.GetType().Name ?? "nothing"}");

    private static NoteDto Created(ActionResult<NoteDto> result) =>
        (NoteDto)((CreatedAtActionResult)result.Result!).Value!;

    /// <summary>One controller, its caller, and a second person to be excluded from everything.</summary>
    private sealed record Harness(
        QuillController Controller,
        QuillContext Db,
        StubCallerIdentity Caller,
        Guid Person,
        Guid Someone,
        FakeTimeProvider Time)
    {
        /// <summary>
        /// Writes a note for whoever, straight to the context - the other
        /// person's notes have to exist without the controller having been
        /// willing to create them.
        /// </summary>
        public async Task<QuillNote> WriteAsync(Guid personId, string title, string body)
        {
            var note = new QuillNote
            {
                PersonId = personId,
                Title = title,
                Body = body,
                CreatedAt = Time.GetUtcNow(),
                UpdatedAt = Time.GetUtcNow(),
            };
            Db.Notes.Add(note);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return note;
        }
    }

    /// <summary>Whoever the test says is holding the phone, including nobody.</summary>
    private sealed class StubCallerIdentity : ICallerIdentity
    {
        public Guid? PersonId { get; set; }

        public Task<EfAuthGrant?> GrantAsync(CancellationToken ct) => Task.FromResult<EfAuthGrant?>(null);

        public Task<Guid?> PersonIdAsync(CancellationToken ct) => Task.FromResult(PersonId);

        /// <summary>Quill never asks for the row - it scopes by id, which is the shape the module's queries want. See ICallerIdentity.PersonAsync.</summary>
        public Task<EfPerson?> PersonAsync(CancellationToken ct) => Task.FromResult<EfPerson?>(null);

        /// <summary>Nor for a key: Quill's notes belong to a person, and a program is not one.</summary>
        public Task<EfApiKey?> ApiKeyAsync(CancellationToken ct) => Task.FromResult<EfApiKey?>(null);

        /// <summary>Nor for the local lane: Quill scopes by PersonId, and local mode's person is deliberately not a row in People.</summary>
        public Task<Actor?> LocalAsync(CancellationToken ct) => Task.FromResult<Actor?>(null);

        public Task<bool> IsProgramAsync(CancellationToken ct) => Task.FromResult(false);

        public Task<string> ActorNameAsync(CancellationToken ct) => Task.FromResult(CallerIdentity.Unattributed);
    }
}

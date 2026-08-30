using Aerie.Api.Common;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Quill;

/// <summary>
/// Quill's slice of the Aerie database: the <c>quill</c> schema, its own
/// migration history, and the one thing no other module context does - a value
/// converter that makes the note text protected at rest.
/// </summary>
public class QuillContext(DbContextOptions<QuillContext> options) : DbContext(options), IModuleContext
{
    public const string Schema = "quill";

    public DbSet<QuillNote> Notes => Set<QuillNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        var note = modelBuilder.Entity<QuillNote>();

        // Protection lives here rather than at the write path on purpose. A
        // module that calls SecretProtector.Protect() in its controller has as
        // many chances to forget as it has write paths, and the one that
        // forgets writes plaintext that reads back perfectly - Unprotect
        // handles an untagged legacy value, so nothing ever fails and nobody
        // ever finds out. As a converter there is no call site to forget: the
        // property is plaintext everywhere in the process and ciphertext
        // everywhere in Postgres, including in a migration's raw SQL and in
        // whatever a future endpoint does.
        //
        // What it costs, and what the module must therefore never do: **no SQL
        // predicate over Title or Body can be correct.** EF translates
        // `Where(n => n.Body.Contains(q))` against the stored bytes, which are
        // obfuscated, so it compiles, runs, and silently matches nothing.
        // Search is a client-side pass over the notes the shell already holds
        // (docs/quill.md); ordering is by UpdatedAt, never by Title. If
        // server-side search ever becomes necessary, it needs a searchable
        // projection that is explicitly *not* protected, and that is a decision
        // with a privacy answer attached rather than an index.
        //
        // Unprotect returns null for a value that will not decode - hand-edited,
        // or written by a newer scheme than this build knows. Empty string
        // rather than a throw, for SecretProtector's own reason: one damaged row
        // should cost that note its text, not throw out of the list that draws
        // every other note.
        var protect = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<string, string>(
            plaintext => SecretProtector.Protect(plaintext),
            stored => SecretProtector.Unprotect(stored) ?? "");

        note.Property(n => n.Title).HasConversion(protect);
        note.Property(n => n.Body).HasConversion(protect);

        base.OnModelCreating(modelBuilder);
    }
}

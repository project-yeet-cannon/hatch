namespace Aerie.Api.Modules.Quill;

/// <summary>
/// A note, whole. There is deliberately no summary shape beside it: the list
/// endpoint returns these, bodies and all.
///
/// That looks wasteful and buys the property the app is for. The shell keeps a
/// local mirror so a note is readable on a plane (docs/quill.md), and a mirror
/// filled from summaries can only offer the notes someone happened to have
/// opened - the one they wrote on Monday and did not re-open is exactly the one
/// missing on Thursday. One request that returns everything makes the mirror
/// complete by construction.
///
/// The tripwire, so it is a decision rather than an oversight: this is one
/// person's notes in one household. When that stops fitting comfortably in one
/// response, the answer is a summary list plus per-note reads *and* a sync
/// cursor for the mirror - not summaries alone, which would quietly take the
/// offline guarantee away.
/// </summary>
/// <param name="Title">Blank for an untitled note. The client renders the placeholder; the server does not invent one.</param>
public record NoteDto(
    Guid Id, string Title, string Body, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// A create or an edit - the same shape, because they mean the same thing here.
/// Both fields overwrite, nulls included: this is one person's own note on one
/// device, so there is no second writer to merge with and nothing to be clever
/// about.
/// </summary>
public record NoteWriteRequest(string? Title, string? Body);

namespace Aerie.Api.Models.Docs;

/// <summary>
/// One entry in the docs browser's document list (see DocsService). Slug is the
/// path under /docs without the .md, forward-slashed; Group is its directory
/// half ("" for a doc at the root), which the sidebar renders as a section.
/// </summary>
public record DocSummary(string Slug, string Title, string Group);

/** Mirrors Aerie.Api/Models/Docs/DocSummary.cs. */
export interface DocSummary {
  /** Path under /docs without the .md, forward-slashed - e.g. "plans/swarm/design". */
  slug: string;
  title: string;
  /** Directory half of the slug, "" for a doc at the docs root. */
  group: string;
}

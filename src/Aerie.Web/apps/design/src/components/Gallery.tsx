import type { ReactNode } from 'react';

/** A gallery page: its title, one line saying what the group is for, and the
    specimens. Nothing else - the page is a frame around the thing being shown. */
export function GalleryPage({ title, blurb, children }: { title: string; blurb: string; children: ReactNode }) {
  return (
    <article className="gallery-page">
      <header className="gallery-page-head">
        <h1>{title}</h1>
        <p className="gallery-blurb">{blurb}</p>
      </header>
      {children}
    </article>
  );
}

/** A named run of specimens inside a page. `note` carries the rule that the
    run exists to state - "a radius written as a number is a bug" - because the
    rule is the part a designer needs and the swatch is only the evidence. */
export function GallerySection({ title, note, children }: { title: string; note?: string; children: ReactNode }) {
  return (
    <section className="gallery-section">
      <h2>{title}</h2>
      {note ? <p className="gallery-note">{note}</p> : null}
      {children}
    </section>
  );
}

/** A token's name, set in the mono face so `--sp-1-5` and `--sp-15` are not the
    same shape at a glance. */
export function TokenName({ name }: { name: string }) {
  return <code className="token-name">{name}</code>;
}

/** The value the browser resolved. Renders nothing until it has one rather than
    holding a dash: an em dash next to a token reads as "this token is unset". */
export function TokenValue({ value }: { value: string | undefined }) {
  if (!value) return null;
  return <span className="token-value">{value}</span>;
}

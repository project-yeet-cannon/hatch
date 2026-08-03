import { useEffect, useState } from 'react';
import type { MouseEvent } from 'react';
import { useLocation, useNavigate, useParams } from 'react-router-dom';
import { getDocContent } from '../api/client';
import { renderMarkdown } from '../lib/markdown';
import type { DocSummary } from '../types';

/** Matches a scheme prefix like "https:", "mailto:" - anything absolute, as opposed to a relative doc link. */
const ABSOLUTE_URL = /^[a-z][a-z0-9+.-]*:/i;

export function DocPage({ docs }: { docs: DocSummary[] }) {
  const { slug = '' } = useParams();
  const { hash } = useLocation();
  const navigate = useNavigate();
  const [html, setHtml] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setHtml(null);
    setError(null);

    getDocContent(slug)
      .then((markdown) => {
        if (!cancelled) setHtml(renderMarkdown(markdown));
      })
      .catch((err: unknown) => {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err));
      });

    return () => {
      cancelled = true;
    };
  }, [slug]);

  useEffect(() => {
    if (html === null) return;
    if (hash) {
      document.getElementById(hash.slice(1))?.scrollIntoView();
    } else {
      window.scrollTo(0, 0);
    }
  }, [html, hash]);

  // Docs only ever link to each other by filename (see e.g.
  // "reverse-proxy-architecture.md" and "../TODO_SWARM.md" in the source
  // markdown) - resolve by basename against the known slugs rather than
  // trying to reconstruct a real relative path, since /docs itself is flat.
  function handleClick(event: MouseEvent<HTMLDivElement>) {
    const anchor = (event.target as HTMLElement).closest('a');
    if (!anchor) return;

    const href = anchor.getAttribute('href');
    if (!href || href.startsWith('#')) return; // same-page anchor - let the browser scroll natively

    if (ABSOLUTE_URL.test(href)) {
      anchor.setAttribute('target', '_blank');
      anchor.setAttribute('rel', 'noopener noreferrer');
      return;
    }

    const [path, linkHash] = href.split('#');
    const targetSlug = (path.split('/').pop() ?? '').replace(/\.md$/i, '');
    if (docs.some((d) => d.slug === targetSlug)) {
      event.preventDefault();
      navigate(`/${targetSlug}${linkHash ? `#${linkHash}` : ''}`);
    }
    // Anything else (e.g. a TODO_*.md link outside /docs) falls through to a normal
    // navigation, which 404s inside this SPA - out of scope for the docs browser.
  }

  if (error) return <p className="text-danger">{error}</p>;
  if (html === null) return <p className="text-muted">Loading…</p>;

  // eslint-disable-next-line react/no-danger -- HTML is sanitized by renderMarkdown (DOMPurify) before this point.
  return <div className="docs-markdown" onClick={handleClick} dangerouslySetInnerHTML={{ __html: html }} />;
}

import { useEffect, useState } from 'react';
import type { MouseEvent } from 'react';
import { useLocation, useNavigate, useParams } from 'react-router-dom';
import { getDocContent } from '../api/client';
import { renderMarkdown } from '../lib/markdown';
import type { DocSummary } from '../types';

/** Matches a scheme prefix like "https:", "mailto:" - anything absolute, as opposed to a relative doc link. */
const ABSOLUTE_URL = /^[a-z][a-z0-9+.-]*:/i;

/**
 * Resolves a relative markdown link against the doc it appears in, the way the
 * filesystem does, and returns the slug it lands on - or null if it climbs out
 * of /docs entirely (a link to src/ or scripts/, which this browser can't serve).
 */
function resolveSlug(currentSlug: string, href: string): string | null {
  const path = href.split('#')[0].split('?')[0];
  if (!path) return null;

  const lastSlash = currentSlug.lastIndexOf('/');
  const segments = path.startsWith('/') || lastSlash < 0 ? [] : currentSlug.slice(0, lastSlash).split('/');

  for (const segment of path.replace(/^\//, '').split('/')) {
    if (segment === '' || segment === '.') continue;
    if (segment === '..') {
      if (segments.length === 0) return null; // above /docs - not ours to resolve
      segments.pop();
      continue;
    }
    segments.push(segment);
  }

  return segments.length === 0 ? null : segments.join('/').replace(/\.md$/i, '');
}

export function DocPage({ docs }: { docs: DocSummary[] }) {
  const { '*': slug = '' } = useParams();
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

  // Docs link to each other by relative path, and /docs is no longer flat (see
  // docs/plans/), so resolve against the current doc's directory rather than
  // matching on basename - two plans can each have a README.md.
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

    const [, linkHash] = href.split('#');
    const targetSlug = resolveSlug(slug, href);
    if (targetSlug !== null && docs.some((d) => d.slug === targetSlug)) {
      event.preventDefault();
      navigate(`/${targetSlug}${linkHash ? `#${linkHash}` : ''}`);
    }
    // Anything else (e.g. a link to scripts/ or src/, outside /docs) falls through
    // to a normal navigation, which 404s inside this SPA - out of scope here.
  }

  if (docs.length > 0 && !docs.some((d) => d.slug === slug)) {
    return <p className="text-muted">Document not found.</p>;
  }
  if (error) return <p className="text-danger">{error}</p>;
  if (html === null) return <p className="text-muted">Loading…</p>;

  // eslint-disable-next-line react/no-danger -- HTML is sanitized by renderMarkdown (DOMPurify) before this point.
  return <div className="docs-markdown" onClick={handleClick} dangerouslySetInnerHTML={{ __html: html }} />;
}

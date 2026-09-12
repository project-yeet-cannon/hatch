import { marked } from 'marked';
import DOMPurify from 'dompurify';

/** Renders markdown to sanitized HTML, safe to hand to dangerouslySetInnerHTML.
    The same pair apps/docs uses; descriptions and comments are stored raw and
    rendered here, so nothing that arrives from the database is trusted markup. */
export function renderMarkdown(source: string): string {
  return DOMPurify.sanitize(marked.parse(source, { async: false }));
}

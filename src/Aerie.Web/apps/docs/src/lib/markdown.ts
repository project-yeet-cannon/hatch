import { marked } from 'marked';
import { gfmHeadingId } from 'marked-gfm-heading-id';
import DOMPurify from 'dompurify';

marked.use(gfmHeadingId());

/** Renders markdown to sanitized HTML, safe to hand to dangerouslySetInnerHTML. */
export function renderMarkdown(source: string): string {
  return DOMPurify.sanitize(marked.parse(source, { async: false }));
}

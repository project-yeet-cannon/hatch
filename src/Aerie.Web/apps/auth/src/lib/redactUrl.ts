/**
 * A scanned QR resolves to `/apps/auth/r/<invite code>`, so in this app - and
 * only in this app - `location.href` is credential material. The logging
 * pipeline is not a place a live invite code may end up: docs/plans/auth.md
 * puts "never store or log a token or an invite code" among the conventions
 * that apply to every phase, and `/api/ui-logs` ships straight to OpenSearch,
 * where a code would outlive its fifteen minutes by however long the index is
 * retained.
 *
 * The same rule is repeated inline in index.html, which logs before any of this
 * bundle has been fetched.
 */
const DEEP_LINK = /(\/apps\/auth\/r\/)[^/?#]+/;

export function redactUrl(value: string): string {
  return value.replace(DEEP_LINK, '$1<redacted>');
}

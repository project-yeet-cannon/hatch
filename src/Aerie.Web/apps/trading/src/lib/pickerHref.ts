/**
 * Where the app picker lives, from the host this page is on.
 *
 * The same derivation `apps/home/src/lib/siblingOrigin.ts` makes in the other
 * direction: `trading.example.org` becomes `https://home.example.org/`. The
 * base domain is not a value this repository may hold (docs/ethos.md), so it
 * is read off the address bar rather than configured - a redeploy under a
 * different domain needs no edit here, and the link follows the domain if it
 * ever changes.
 *
 * `/` where there is no domain to borrow: an IPv4 literal has labels, but they
 * are octets, so `home.2.3.4` is not an address; a bare IPv6 and a single
 * label like `localhost` are the same problem. On this host `/` is this app
 * itself, which is a worse link than the picker and a better one than a
 * hostname that does not resolve.
 */
export function pickerHref(hostname: string): string {
  if (/^\d+(\.\d+){3}$/.test(hostname) || hostname.includes(':')) return '/';
  const labels = hostname.split('.');
  if (labels.length < 2) return '/';
  return `https://home.${labels.slice(1).join('.')}/`;
}

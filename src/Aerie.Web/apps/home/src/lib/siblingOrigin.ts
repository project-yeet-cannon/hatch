/**
 * The address of a sibling service on the same base domain, derived from the
 * host this page is being served on.
 *
 * Four of the entries on the picker are not same-origin: the file share and
 * the three observability services are each their own container behind their
 * own login, on their own subdomain. Aerie ships to other operators, so the
 * base domain is not a value this repo may hold (docs/ethos.md) - it is read
 * back off the address bar instead. `home.example.org` becomes
 * `metrics.example.org`, which means a redeploy under a different domain needs
 * no edit here and follows the domain if it ever changes.
 *
 * `null` where there is no domain to borrow: an IP address, or a single label
 * like `localhost` during development. The service exists, but not at any
 * address this page can name, and guessing one would produce a link that goes
 * nowhere. The caller shows the entry without a link rather than hiding it -
 * "this is here, but not from where you are standing" is the true sentence.
 */
export function siblingOrigin(hostname: string, subdomain: string): string | null {
  /* An IPv4 literal has labels, but they are octets - `1.2.3.4` would yield
     `https://logs.2.3.4/`. IPv6 arrives bracketed in a URL but bare from
     location.hostname, so it is caught by the colon rather than by shape. */
  if (/^\d+(\.\d+){3}$/.test(hostname) || hostname.includes(':')) return null;

  const labels = hostname.split('.');
  if (labels.length < 2) return null;

  return `https://${subdomain}.${labels.slice(1).join('.')}/`;
}

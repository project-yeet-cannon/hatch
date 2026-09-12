/**
 * Where this bundle is mounted, as far as the browser is concerned.
 *
 * Hatch answers at two addresses, and React Router's `basename` has to agree
 * with whichever one the address bar is showing:
 *
 *   `home.${DOMAIN}/apps/hatch/...` - the bundle in its own right, under /apps
 *   like every other app in the house.
 *
 *   `hatch.${DOMAIN}/...` - the same bundle, reached through the rewrite in
 *   charts/hatch/templates/middleware-hatch.yaml.
 *
 * That rewrite is deliberately server-side, so on the subdomain the path the
 * browser holds is `/issues/AER-12` while the path the API answered is
 * `/apps/hatch/issues/AER-12` - the hostname exists precisely so a ticket link
 * never has to name the prefix. Which means a fixed basename can only ever be
 * right on one of the two: with `/apps/hatch` hardcoded, the subdomain rendered
 * nothing at all, and said so only in a console warning ('<Router
 * basename="/apps/hatch"> is not able to match the URL "/"') with a clean
 * network panel behind it.
 *
 * Matched by whole segment rather than by prefix, the same rule
 * AdminAppMiddleware's gate uses: a later /apps/hatchery would not be this app.
 */
export const APP_BASENAME = '/apps/hatch';

export function routerBasename(pathname: string): string {
  return pathname === APP_BASENAME || pathname.startsWith(`${APP_BASENAME}/`)
    ? APP_BASENAME
    : '/';
}

/**
 * An in-app route as an href the browser can follow on its own.
 *
 * <Link> applies the basename; a raw anchor does not, and there is one place
 * that needs a raw anchor - the summary dialog's "open in a new tab", where
 * `target="_blank"` is the whole point and React Router will not open a second
 * document. On the subdomain this is the identity, and on the house hostname it
 * is what keeps the new tab from landing on /issues/AER-12 with nothing served
 * there.
 */
export function hrefWithin(basename: string, path: string): string {
  return basename === '/' ? path : `${basename}${path}`;
}

/** The same, against whatever address this document was actually served at. */
export const appHref = (path: string): string =>
  hrefWithin(routerBasename(window.location.pathname), path);

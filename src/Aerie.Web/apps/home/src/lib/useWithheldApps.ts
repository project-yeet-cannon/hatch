import { useEffect, useState } from 'react';
import { TIERS } from '../apps';

/**
 * The apps that are on this install but not for this person, by name.
 *
 * Admin is withheld from anyone whose device is not linked to an administrator
 * - a flat 404 on the whole bundle, indistinguishable from an install that
 * never built it (docs/auth-architecture.md, "The admin flag"). A tile linking
 * to that 404 undoes it: it says the app is there and that you are not welcome
 * in it, which is the sentence the 404 was chosen to avoid. So the tile is
 * dropped rather than greyed out - unlike a sibling service with no domain to
 * borrow, where "this exists, but not at an address this page can name" is
 * true and worth saying. Here there is nothing to point at.
 *
 * A failed request leaves the tile alone. A fetch that never answers means the
 * page is offline or the API is down, and hiding an app over a network blip is
 * a worse outcome than a link that 404s once.
 */
export function useWithheldApps(): ReadonlySet<string> {
  const [withheld, setWithheld] = useState<ReadonlySet<string>>(() => new Set<string>());

  useEffect(() => {
    const guarded = TIERS.flatMap((tier) => tier.apps).flatMap((app) =>
      app.withheldIf404 && app.href ? [{ name: app.name, href: app.href }] : [],
    );
    if (guarded.length === 0) return;

    /* React 19 aborts on unmount rather than setting state on a dead tree; in
       StrictMode's double-invoked development mount this is what stops the
       first pass from landing after the second has replaced it. */
    const controller = new AbortController();

    void Promise.all(
      guarded.map(async ({ name, href }) => {
        try {
          const response = await fetch(href, { method: 'HEAD', signal: controller.signal });
          return response.status === 404 ? name : null;
        } catch {
          return null;
        }
      }),
    ).then((names) => {
      if (controller.signal.aborted) return;
      const hidden = names.filter((name) => name !== null);
      if (hidden.length > 0) setWithheld(new Set(hidden));
    });

    return () => controller.abort();
  }, []);

  return withheld;
}

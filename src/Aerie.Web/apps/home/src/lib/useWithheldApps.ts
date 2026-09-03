import { useEffect, useState } from 'react';
import { TIERS } from '../apps';

/**
 * The apps that are on this install but not for this person, by name.
 *
 * The operator's own apps - Admin, and Hatch - are withheld from anyone whose
 * device is not linked to an administrator: a flat 404 on the whole bundle,
 * indistinguishable from an install that never built it
 * (docs/auth-architecture.md, "The admin flag"). A tile linking to that 404
 * undoes it: it says the app is there and that you are not welcome in it,
 * which is the sentence the 404 was chosen to avoid. So the tile is dropped
 * rather than greyed out - unlike a sibling service with no domain to borrow,
 * where "this exists, but not at an address this page can name" is true and
 * worth saying. Here there is nothing to point at.
 *
 * What is probed is a *same-origin* path, always. An app on its own subdomain
 * still has a bundle served by this pod (Hatch is at hatch.${DOMAIN} and
 * /apps/hatch/), and asking the subdomain directly would answer for the wrong
 * reasons - a cross-origin HEAD is opaque, and the wall in front of that host
 * refuses before the admin gate ever runs. So an entry names `probePath` when
 * `href` is not the thing to ask about.
 *
 * A failed request leaves the tile alone. A fetch that never answers means the
 * page is offline or the API is down, and hiding an app over a network blip is
 * a worse outcome than a link that 404s once.
 */
export function useWithheldApps(): ReadonlySet<string> {
  const [withheld, setWithheld] = useState<ReadonlySet<string>>(() => new Set<string>());

  useEffect(() => {
    const guarded = TIERS.flatMap((tier) => tier.apps).flatMap((app) => {
      const probe = app.probePath ?? app.href;
      return app.withheldIf404 && probe ? [{ name: app.name, probe }] : [];
    });
    if (guarded.length === 0) return;

    /* React 19 aborts on unmount rather than setting state on a dead tree; in
       StrictMode's double-invoked development mount this is what stops the
       first pass from landing after the second has replaced it. */
    const controller = new AbortController();

    void Promise.all(
      guarded.map(async ({ name, probe }) => {
        try {
          const response = await fetch(probe, { method: 'HEAD', signal: controller.signal });
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

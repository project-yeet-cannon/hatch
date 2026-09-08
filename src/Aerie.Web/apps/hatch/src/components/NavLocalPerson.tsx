import { useEffect, useState } from 'react';
import { getLocalPerson } from '../api/client';
import type { LocalPerson } from '../types';

/**
 * What to say when Hatch does not know who is sitting here.
 *
 * Exported as one constant so that AERIE-936, which adds the Settings page this
 * points at, has a single string to replace with a link rather than a sentence
 * to find inside a component.
 */
export const UNNAMED_HINT = 'Hatch does not know your name. Set Auth__LocalPerson__Name and restart.';

/**
 * Who Hatch thinks is at this machine, in the nav strip.
 *
 * Draws nothing at all wherever there is a wall, which is every cluster
 * install: the route answers 204 there, and a strip with an empty box in it
 * would be worse than a strip with nothing. The same call `NavUtilization`
 * makes about a Claude token, for the same reason.
 *
 * Read once. The name comes from config or a site setting, and neither moves
 * without somebody doing something that reloads this page anyway - so a poll
 * would be a request a minute answering a question nobody asked. AERIE-936's
 * Settings page is what will need to move it, and moving it is that story's.
 *
 * Nothing here reports an error. A failure to reach Hatch's own endpoint leaves
 * the strip as it was; a nav strip is not where a fetch failure gets announced.
 */
export function NavLocalPerson() {
  const [person, setPerson] = useState<LocalPerson | null>(null);

  useEffect(() => {
    let cancelled = false;

    void (async () => {
      try {
        const answer = await getLocalPerson();
        if (!cancelled) setPerson(answer);
      } catch {
        // Deliberately silent - see the note above.
      }
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  if (person === null) return null;

  return (
    <span
      className={`hatch-local-person${person.configured ? '' : ' hatch-local-person-unnamed'}`}
      title={person.configured ? undefined : UNNAMED_HINT}
    >
      <span className="hatch-local-person-name">{person.name}</span>
      {/* The hint is drawn as well as being the tooltip: an operator who has
          just started Aerie for the first time is exactly the person who will
          not think to hover over their own name. */}
      {person.configured ? null : <span className="hatch-local-person-hint">{UNNAMED_HINT}</span>}
    </span>
  );
}

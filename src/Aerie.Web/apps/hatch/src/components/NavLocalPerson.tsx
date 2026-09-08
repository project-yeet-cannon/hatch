import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { getLocalPerson } from '../api/client';
import { LOCAL_PERSON_CHANGED } from '../lib/localPerson';
import type { LocalPerson } from '../types';

/**
 * What to say when Hatch does not know who is sitting here.
 *
 * No longer says what to set, because there is now somewhere to press: the
 * rendered hint carries a link to the Settings page beside this sentence. It
 * stays a single exported constant because it is also the tooltip, and a
 * tooltip cannot hold a link - so the sentence has to read on its own.
 */
export const UNNAMED_HINT = 'Hatch does not know your name.';

/**
 * Who Hatch thinks is at this machine, in the nav strip.
 *
 * Draws nothing at all wherever there is a wall, which is every cluster
 * install: the route answers 204 there, and a strip with an empty box in it
 * would be worse than a strip with nothing. The same call `NavUtilization`
 * makes about a Claude token, for the same reason.
 *
 * Read on mount, and again whenever the Settings page says the name changed.
 * There is exactly one thing in the app that can move it, and it announces
 * itself (lib/localPerson.ts) - so this needs no poll, which would be a request
 * a minute answering a question nobody asked.
 *
 * Nothing here reports an error. A failure to reach Hatch's own endpoint leaves
 * the strip as it was; a nav strip is not where a fetch failure gets announced.
 */
export function NavLocalPerson() {
  const [person, setPerson] = useState<LocalPerson | null>(null);

  useEffect(() => {
    let cancelled = false;

    const read = async () => {
      try {
        const answer = await getLocalPerson();
        if (!cancelled) setPerson(answer);
      } catch {
        // Deliberately silent - see the note above.
      }
    };

    void read();

    const onChanged = () => void read();
    window.addEventListener(LOCAL_PERSON_CHANGED, onChanged);

    return () => {
      cancelled = true;
      window.removeEventListener(LOCAL_PERSON_CHANGED, onChanged);
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
      {person.configured ? null : (
        <span className="hatch-local-person-hint">
          {UNNAMED_HINT} <Link to="/settings">Set it</Link>
        </span>
      )}
    </span>
  );
}

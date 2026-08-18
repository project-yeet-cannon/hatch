import { library, findIconDefinition, type IconDefinition, type IconName } from '@fortawesome/fontawesome-svg-core';
import { fas, faBolt } from '@fortawesome/free-solid-svg-icons';

library.add(fas);

// The `fas` pack exports many legacy-alias keys (faDollar, faUsd, faSyncAlt, ...) that resolve to
// the same iconName as a canonical key (dollar-sign, rotate, ...). Dedupe by iconName so aliases
// don't pad out the results with repeats of an icon already shown.
const allIcons: IconDefinition[] = Array.from(
  new Map(Object.values(fas).map((icon) => [icon.iconName, icon])).values(),
);

/**
 * Resolves a Routine's stored icon name (e.g. "lightbulb") to its FA definition, falling back to a
 * generic icon when unset or unrecognized. The name is data-driven (stored per-Routine, not known
 * statically), hence the cast to IconName - findIconDefinition validates it against the registered
 * pack at runtime and returns undefined on a miss, which the fallback handles.
 */
export function iconFor(name: string | null): IconDefinition {
  if (!name) return faBolt;
  return findIconDefinition({ prefix: 'fas', iconName: name as IconName }) ?? faBolt;
}

/**
 * Case-insensitive substring search over every solid icon's name, capped so the picker's grid stays
 * cheap to render. Ranked so an exact or prefix match (e.g. "bed" -> bed) always beats an unrelated
 * icon that merely contains the query as a substring (e.g. "bed" -> cart-flatbed), rather than
 * relying on the pack's arbitrary internal key order.
 */
export function searchIcons(query: string, limit = 60): IconDefinition[] {
  const q = query.trim().toLowerCase();
  if (q === '') return allIcons.slice(0, limit);

  const rank = (name: string) => (name === q ? 0 : name.startsWith(q) ? 1 : 2);

  return allIcons
    .filter((icon) => icon.iconName.toLowerCase().includes(q))
    .sort((a, b) => rank(a.iconName) - rank(b.iconName) || a.iconName.localeCompare(b.iconName))
    .slice(0, limit);
}

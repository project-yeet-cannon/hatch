import { library, findIconDefinition, type IconDefinition, type IconName } from '@fortawesome/fontawesome-svg-core';
import { fas, faBolt } from '@fortawesome/free-solid-svg-icons';

library.add(fas);

const allIcons: IconDefinition[] = Object.values(fas);

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

/** Case-insensitive substring search over every solid icon's name, capped so the picker's grid stays cheap to render. */
export function searchIcons(query: string, limit = 60): IconDefinition[] {
  const q = query.trim().toLowerCase();
  const matches = q === '' ? allIcons : allIcons.filter((icon) => icon.iconName.toLowerCase().includes(q));
  return matches.slice(0, limit);
}

import type { ComponentType } from 'react';
import { ColorPage } from './pages/ColorPage';
import { TypePage } from './pages/TypePage';
import { SpacingPage } from './pages/SpacingPage';
import { RadiusPage } from './pages/RadiusPage';
import { ElevationPage } from './pages/ElevationPage';
import { SeriesPage } from './pages/SeriesPage';

export interface Section {
  /** The nav heading this section files under. Groups render in the order
      they first appear below, so adding a section is one entry, not two. */
  group: string;
  /** The URL segment, and the key everything else is derived from. */
  slug: string;
  /** What the nav shows. */
  title: string;
  Page: ComponentType;
}

/**
 * Every page in the gallery, in nav order.
 *
 * This is the file Phase 3 and Phase 4 edit: a component lands in @aerie/ui,
 * its page lands in pages/, and one entry here puts it in the nav, on a route
 * and behind a deep link. Nothing else in the app enumerates the sections.
 *
 * There is no "Components" group yet because there are no components to show,
 * and an empty group in the nav would be a promise the app cannot keep -
 * renders-nothing discipline applies to the gallery's own chrome first.
 */
export const SECTIONS: Section[] = [
  { group: 'Foundations', slug: 'color', title: 'Color', Page: ColorPage },
  { group: 'Foundations', slug: 'type', title: 'Type', Page: TypePage },
  { group: 'Foundations', slug: 'spacing', title: 'Spacing', Page: SpacingPage },
  { group: 'Foundations', slug: 'radius', title: 'Radius', Page: RadiusPage },
  { group: 'Foundations', slug: 'elevation', title: 'Elevation & motion', Page: ElevationPage },
  { group: 'Foundations', slug: 'series', title: 'Series', Page: SeriesPage },
];

/** The first section, and so where `/` and any unknown route land. */
export const DEFAULT_SLUG = SECTIONS[0].slug;

/** The sections grouped for the nav, preserving first-appearance order. */
export function groupedSections(): { group: string; sections: Section[] }[] {
  const groups: { group: string; sections: Section[] }[] = [];
  for (const section of SECTIONS) {
    const existing = groups.find((candidate) => candidate.group === section.group);
    if (existing) {
      existing.sections.push(section);
    } else {
      groups.push({ group: section.group, sections: [section] });
    }
  }
  return groups;
}

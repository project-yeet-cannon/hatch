import { lazy } from 'react';
import type { ComponentType, LazyExoticComponent } from 'react';

export interface FamilyModule {
  /** URL segment under /apps/family/, and the module folder name. */
  id: string;
  title: string;
  /** One line, shown on the home screen card. */
  tagline: string;
  icon: string;
  /**
   * Owns everything under /apps/family/{id}/ - it renders its own <Routes>,
   * so the shell never learns a module's internal screens.
   */
  Component: LazyExoticComponent<ComponentType>;
}

/**
 * Every app in the shell. Adding one is this array plus a folder next to this
 * file - no route table to edit, no nav markup to touch, and nothing at all in
 * Program.cs, the Dockerfile, CI, or any deployment manifest.
 *
 * `lazy` is what keeps that true at ten apps: each module is its own chunk, so
 * a rarely-opened app costs nothing on launch.
 */
export const modules: FamilyModule[] = [
  {
    id: 'storage',
    title: 'Storage',
    tagline: 'Crates, labels, and where the drill went',
    icon: '📦',
    Component: lazy(() => import('./storage/StorageApp')),
  },
  {
    id: 'gather',
    title: 'Gather',
    tagline: "Milk, batteries, and what we're out of",
    icon: '🧺',
    Component: lazy(() => import('./gather/GatherApp')),
  },
];

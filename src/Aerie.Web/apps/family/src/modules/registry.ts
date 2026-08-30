import { lazy } from 'react';
import type { ComponentType, LazyExoticComponent } from 'react';
import type { Session } from '../lib/session';

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

  /**
   * Only exists for a session with a person behind it - no card, no tab, no
   * route (see {@link visibleModules}).
   *
   * One flag rather than a predicate, deliberately. The shell's job is to know
   * that a module has an owner-shaped requirement, not to host a module's
   * access rules: the moment this becomes `canSee: (session) => boolean`, the
   * decision about who may read a module's data lives in the shell instead of
   * behind the API that actually enforces it, in a file a module author is
   * invited to edit. Hiding is courtesy - the API refuses independently and
   * identically (Modules/Quill/QuillController.cs).
   */
  requiresPerson?: boolean;
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
  {
    id: 'game',
    title: 'Game',
    tagline: 'Say what happens next, and it happens',
    icon: '🎮',
    Component: lazy(() => import('./game/GameApp')),
  },
  {
    id: 'quill',
    title: 'Quill',
    tagline: 'Yours, and nobody else\'s',
    icon: '🪶',
    Component: lazy(() => import('./quill/QuillApp')),
    requiresPerson: true,
  },
];

/**
 * The modules this session can see. Everything the shell draws - cards, tabs,
 * routes - comes from this rather than from `modules`, so a module that
 * requires an owner cannot be reached by a device nobody has claimed, including
 * by typing its URL.
 *
 * A deep link into a hidden module lands on the shell's "nothing here" screen,
 * which is the same thing a stale bookmark for a deleted module gets. That is
 * the intended answer: a device with no owner should not be able to tell that
 * Quill exists, and a 403-shaped "you may not" would tell it.
 */
export function visibleModules(session: Session | null): FamilyModule[] {
  return modules.filter((module) => !module.requiresPerson || session?.personId != null);
}

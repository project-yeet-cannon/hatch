/* The theme mechanism, with no React in it.
   ---------------------------------------------------------------------------
   `auto | light | dark`, persisted, resolved, and written to `<html
   data-theme>` - which is what tokens.css keys its dark palette on.

   This file is framework-free on purpose. <ThemeProvider> is one caller; the
   other is standalone/topbar.ts, the bar that renders on pages Hatch did not
   build with Vite and React (Swagger UI, the logo export). Those pages must
   read and write the *same* stored choice as the apps - a house where the
   theme you picked in admin does not survive the click into Swagger is a house
   with two themes - and importing ThemeProvider to get it would drag React
   onto a static page to run twelve lines of localStorage.

   So the mechanism lives here and both callers use it. The React file below
   holds only what is genuinely React: state, an effect, a context. */

/** What the operator chose. `auto` defers to the OS preference. */
export type ThemeChoice = 'auto' | 'light' | 'dark';

/** What that choice resolves to right now. `auto` collapses to one of these. */
export type ResolvedTheme = 'light' | 'dark';

/** Where the choice is persisted. Namespaced - apps share an origin. */
export const THEME_STORAGE_KEY = 'hatch.theme';

export const DARK_QUERY = '(prefers-color-scheme: dark)';

export function isThemeChoice(value: unknown): value is ThemeChoice {
  return value === 'auto' || value === 'light' || value === 'dark';
}

/**
 * The stored choice, or `auto`.
 *
 * localStorage *throws* rather than returning null in a partitioned or
 * locked-down context (Safari private browsing, a third-party frame). The page
 * still has to render, so an unreadable store means `auto`.
 */
export function readChoice(): ThemeChoice {
  try {
    const stored = window.localStorage.getItem(THEME_STORAGE_KEY);
    return isThemeChoice(stored) ? stored : 'auto';
  } catch {
    return 'auto';
  }
}

/**
 * Persists the choice. A store that cannot be written is not an error worth
 * surfacing: the choice holds for this tab and is forgotten on reload, which is
 * better than refusing to change theme at all.
 */
export function writeChoice(choice: ThemeChoice): void {
  try {
    window.localStorage.setItem(THEME_STORAGE_KEY, choice);
  } catch {
    /* See above. */
  }
}

export function prefersDark(): boolean {
  return window.matchMedia(DARK_QUERY).matches;
}

/**
 * Writes the choice to `<html data-theme>`.
 *
 * `auto` *removes* the attribute rather than writing the resolved value: with
 * no attribute the `prefers-color-scheme` guard in tokens.css governs, so an OS
 * that changes theme while the tab is backgrounded is already correct on the
 * next paint, with no JavaScript involved.
 */
export function applyChoice(choice: ThemeChoice): void {
  const root = document.documentElement;
  if (choice === 'auto') {
    root.removeAttribute('data-theme');
  } else {
    root.setAttribute('data-theme', choice);
  }
}

/** Calls back when the OS preference flips. Returns the unsubscribe. */
export function watchSystemTheme(onChange: (dark: boolean) => void): () => void {
  const query = window.matchMedia(DARK_QUERY);
  const listener = (event: MediaQueryListEvent) => onChange(event.matches);
  query.addEventListener('change', listener);
  return () => query.removeEventListener('change', listener);
}

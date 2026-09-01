import { useCallback, useEffect, useLayoutEffect, useMemo, useState, type ReactNode } from 'react';
import {
  DARK_QUERY,
  isThemeChoice,
  THEME_STORAGE_KEY,
  ThemeContext,
  type ThemeChoice,
  type ThemeContextValue,
  type ResolvedTheme,
} from './themeContext';

function readStoredChoice(): ThemeChoice {
  /* localStorage throws rather than returning null in a partitioned or
     locked-down context (Safari private browsing, a third-party frame). The
     app still has to render, so an unreadable store means `auto`. */
  try {
    const stored = window.localStorage.getItem(THEME_STORAGE_KEY);
    return isThemeChoice(stored) ? stored : 'auto';
  } catch {
    return 'auto';
  }
}

function prefersDark(): boolean {
  return window.matchMedia(DARK_QUERY).matches;
}

/**
 * Resolves `auto | light | dark` and writes it to `<html data-theme>`, which is
 * what @aerie/ui's tokens.css keys its dark palette on.
 *
 * `auto` *removes* the attribute rather than writing the resolved value: with
 * no attribute the `prefers-color-scheme` guard governs, so an OS that changes
 * theme while the tab is backgrounded is already correct on the next paint,
 * with no JavaScript involved. The listener below exists for the resolved
 * value React components read, not for the CSS.
 */
export function ThemeProvider({ children }: { children: ReactNode }) {
  const [choice, setStoredChoice] = useState<ThemeChoice>(readStoredChoice);
  const [systemDark, setSystemDark] = useState<boolean>(prefersDark);

  useEffect(() => {
    const query = window.matchMedia(DARK_QUERY);
    const onChange = (event: MediaQueryListEvent) => setSystemDark(event.matches);
    query.addEventListener('change', onChange);
    /* The OS may have flipped between the initial state and this effect. */
    setSystemDark(query.matches);
    return () => query.removeEventListener('change', onChange);
  }, []);

  const resolved: ResolvedTheme = choice === 'auto' ? (systemDark ? 'dark' : 'light') : choice;

  /* Layout, not passive: this runs before the browser paints, so a stored
     `dark` on a light-preference machine never shows a light frame first. */
  useLayoutEffect(() => {
    const root = document.documentElement;
    if (choice === 'auto') {
      root.removeAttribute('data-theme');
    } else {
      root.setAttribute('data-theme', choice);
    }
  }, [choice]);

  const setChoice = useCallback((next: ThemeChoice) => {
    setStoredChoice(next);
    try {
      window.localStorage.setItem(THEME_STORAGE_KEY, next);
    } catch {
      /* Unwritable store: the choice holds for this tab and is forgotten on
         reload, which is better than refusing to change theme at all. */
    }
  }, []);

  const value = useMemo<ThemeContextValue>(
    () => ({ choice, resolved, setChoice }),
    [choice, resolved, setChoice],
  );

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

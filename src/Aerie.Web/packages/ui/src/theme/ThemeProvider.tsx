import { useCallback, useEffect, useLayoutEffect, useMemo, useState, type ReactNode } from 'react';
import { ThemeContext, type ThemeContextValue } from './themeContext';
import {
  applyChoice,
  prefersDark,
  readChoice,
  watchSystemTheme,
  writeChoice,
  type ResolvedTheme,
  type ThemeChoice,
} from './themeStore';

/**
 * Resolves `auto | light | dark` and writes it to `<html data-theme>`, which is
 * what @aerie/ui's tokens.css keys its dark palette on.
 *
 * The reading, writing and resolving are themeStore.ts - shared verbatim with
 * the standalone bar, so a theme chosen in admin is the theme Swagger opens
 * with. What is here is the React around it: the state the components read,
 * the subscription, and the context.
 */
export function ThemeProvider({ children }: { children: ReactNode }) {
  const [choice, setStoredChoice] = useState<ThemeChoice>(readChoice);
  const [systemDark, setSystemDark] = useState<boolean>(prefersDark);

  useEffect(() => {
    const unwatch = watchSystemTheme(setSystemDark);
    /* The OS may have flipped between the initial state and this effect. */
    setSystemDark(prefersDark());
    return unwatch;
  }, []);

  const resolved: ResolvedTheme = choice === 'auto' ? (systemDark ? 'dark' : 'light') : choice;

  /* Layout, not passive: this runs before the browser paints, so a stored
     `dark` on a light-preference machine never shows a light frame first. */
  useLayoutEffect(() => {
    applyChoice(choice);
  }, [choice]);

  const setChoice = useCallback((next: ThemeChoice) => {
    setStoredChoice(next);
    writeChoice(next);
  }, []);

  const value = useMemo<ThemeContextValue>(
    () => ({ choice, resolved, setChoice }),
    [choice, resolved, setChoice],
  );

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

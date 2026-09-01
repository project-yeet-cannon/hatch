import { createContext } from 'react';

/** What the operator chose. `auto` defers to the OS preference. */
export type ThemeChoice = 'auto' | 'light' | 'dark';

/** What that choice resolves to right now. `auto` collapses to one of these. */
export type ResolvedTheme = 'light' | 'dark';

export interface ThemeContextValue {
  /** The stored choice, including `auto`. This is what a toggle renders. */
  choice: ThemeChoice;
  /** The theme actually on screen. This is what a component branches on. */
  resolved: ResolvedTheme;
  setChoice: (choice: ThemeChoice) => void;
}

/* Undefined rather than a default value: a component reading the theme outside
   a provider is a wiring mistake, and useTheme says so rather than quietly
   rendering the light one. */
export const ThemeContext = createContext<ThemeContextValue | undefined>(undefined);

/** Where the choice is persisted. Namespaced - apps share an origin. */
export const THEME_STORAGE_KEY = 'aerie.theme';

export const DARK_QUERY = '(prefers-color-scheme: dark)';

export function isThemeChoice(value: unknown): value is ThemeChoice {
  return value === 'auto' || value === 'light' || value === 'dark';
}

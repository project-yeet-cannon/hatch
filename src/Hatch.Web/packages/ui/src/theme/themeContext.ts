import { createContext } from 'react';
import type { ResolvedTheme, ThemeChoice } from './themeStore';

/* The choice type, the storage key, the media query and the guard live in
   themeStore.ts, which has no React in it so the standalone bar can share
   them. They are re-exported here because this is where the app-facing theme
   types have always been imported from. */
export type { ThemeChoice, ResolvedTheme } from './themeStore';
export { THEME_STORAGE_KEY, DARK_QUERY, isThemeChoice } from './themeStore';

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

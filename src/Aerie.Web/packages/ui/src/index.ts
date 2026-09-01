/* @aerie/ui - the shared vocabulary and components for Aerie's web apps.
   The tokens are a separate entry point (`@aerie/ui/tokens.css`) because a
   stylesheet is imported by the app's own stylesheet, not by its JavaScript. */
export { ThemeProvider } from './theme/ThemeProvider';
export { useTheme } from './theme/useTheme';
export type { ThemeChoice, ResolvedTheme, ThemeContextValue } from './theme/themeContext';

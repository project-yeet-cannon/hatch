/* @aerie/ui - the shared vocabulary and components for Aerie's web apps.
   The tokens are a separate entry point (`@aerie/ui/tokens.css`) because a
   stylesheet is imported by the app's own stylesheet, not by its JavaScript.

   A component's own styles are NOT a separate entry point: each component
   imports its colocated .css itself, so adding a component to an app is one
   import rather than two.

   The cost of that, knowingly: a CSS import is a side effect, so a barrel
   export puts every component's styles in every consuming app's bundle even
   where the component is tree-shaken out of the JavaScript. Admin carries
   ThemeSwitch's rules today without rendering it. That is a kilobyte, it goes
   away in Phase 3 when admin's top bar renders the switch, and admin is slated
   to use all nine Phase 4 primitives - so per-component entry points would buy
   nothing and cost every import site an extra specifier. Revisit if a consumer
   ever wants a genuinely small slice of the library. */
export { ThemeProvider } from './theme/ThemeProvider';
export { useTheme } from './theme/useTheme';
export type { ThemeChoice, ResolvedTheme, ThemeContextValue } from './theme/themeContext';
export { ThemeSwitch } from './components/ThemeSwitch';

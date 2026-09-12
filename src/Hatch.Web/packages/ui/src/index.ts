/* @hatch/ui - the shared vocabulary and components for Hatch's web apps.
   The tokens are a separate entry point (`@hatch/ui/tokens.css`) because a
   stylesheet is imported by the app's own stylesheet, not by its JavaScript.

   A component's own styles are NOT a separate entry point: each component
   imports its colocated .css itself, so adding a component to an app is one
   import rather than two.

   The cost of that, knowingly: a CSS import is a side effect, so a barrel
   export puts every component's styles in every consuming app's bundle even
   where the component is tree-shaken out of the JavaScript. Both consumers now
   render the whole library - the top bar brought the theme switch and the app
   switcher with it - and admin is slated to use all nine Phase 4 primitives,
   so per-component entry points would buy nothing and cost every import site
   an extra specifier. Revisit if a consumer ever wants a genuinely small slice
   of the library. */
export { ThemeProvider } from './theme/ThemeProvider';
export { useTheme } from './theme/useTheme';
export type { ThemeChoice, ResolvedTheme, ThemeContextValue } from './theme/themeContext';
export { ThemeSwitch } from './components/ThemeSwitch';
export type { ThemeSwitchTone } from './components/ThemeSwitch';
export { TopBar } from './components/TopBar';
export type { TopBarProps } from './components/TopBar';
export { AppSwitcher } from './components/AppSwitcher';
export type { AppSwitcherProps } from './components/AppSwitcher';

/* The primitives. Ordered the way a page is built rather than alphabetically:
   the frame first, then what goes in it, then what it says. */
export { PageHeader } from './components/PageHeader';
export type { PageHeaderProps, PageHeaderLevel } from './components/PageHeader';
export { Card } from './components/Card';
export type { CardProps } from './components/Card';
export { Grid } from './components/Grid';
export type { GridProps, GridCols } from './components/Grid';
export { Table } from './components/Table';
export type { TableProps } from './components/Table';
export { Modal } from './components/Modal';
export type { ModalProps } from './components/Modal';
export { Field } from './components/Field';
export type { FieldProps } from './components/Field';
export { Button } from './components/Button';
export type { ButtonProps, ButtonVariant } from './components/Button';
export { Badge } from './components/Badge';
export type { BadgeProps, BadgeTone } from './components/Badge';
export { Text } from './components/Text';
export type { TextProps, TextTone } from './components/Text';
export { EmptyState } from './components/EmptyState';
export type { EmptyStateProps } from './components/EmptyState';

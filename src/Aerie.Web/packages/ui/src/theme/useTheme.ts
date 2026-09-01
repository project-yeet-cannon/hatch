import { useContext } from 'react';
import { ThemeContext, type ThemeContextValue } from './themeContext';

/**
 * The current theme and the control for it.
 *
 * Throws outside a <ThemeProvider> rather than falling back to light: a
 * silently-light toggle is harder to find than a stack trace.
 */
export function useTheme(): ThemeContextValue {
  const value = useContext(ThemeContext);
  if (value === undefined) {
    throw new Error('useTheme must be used inside a <ThemeProvider>');
  }
  return value;
}

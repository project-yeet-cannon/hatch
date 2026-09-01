import { AppsMark } from './AppsMark';
import './AppSwitcher.css';

export interface AppSwitcherProps {
  /** Where the app picker lives. `/` redirects to it, and stays the picker's
      address through Phase 5's move, so it is the default rather than a
      literal `/apps/`. */
  href?: string;
  /** The accessible name, and the tooltip. Overridable because an app that is
      not one of many - a kiosk shell, say - may want to say something else. */
  label?: string;
  className?: string;
}

/**
 * The link back to the app picker: a real control with a hit target, an
 * accessible name and a focus ring, in place of the bare `▦` character admin
 * used to render as a link.
 *
 * A component rather than markup inside <TopBar> because the picker link is the
 * one piece of the bar every app shares verbatim, and because the gallery shows
 * it on its own - a hit target is a thing you look at in isolation to judge.
 *
 * An `<a>`, not a button with an onClick: it navigates, so middle-click, cmd-
 * click and "copy link address" all have to work, and only an anchor gives
 * those for free.
 */
export function AppSwitcher({ href = '/', label = 'All apps', className }: AppSwitcherProps) {
  return (
    <a
      className={className ? `aerie-app-switcher ${className}` : 'aerie-app-switcher'}
      href={href}
      aria-label={label}
      title={label}
    >
      <AppsMark />
    </a>
  );
}

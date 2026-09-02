import { AppsMark } from './AppsMark';
import './AppSwitcher.css';

export interface AppSwitcherProps {
  /** Where the app picker lives. The default is `/` rather than a literal
      `/apps/home/` because `/` is the address that survives the picker moving:
      it redirected to a static index.html in wwwroot/apps before Phase 5 and
      redirects to the picker app now, and every bar in the house kept working
      across that move without an edit. */
  href?: string;
  /** The accessible name, and the tooltip. Overridable because an app that is
      not one of many - a kiosk shell, say - may want to say something else. */
  label?: string;
  /** The home state: this app *is* the picker, so the mark stays where every
      other app puts it and stops being a link. See the note below. */
  current?: boolean;
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
 *
 * **`current` is the home state**, and it is why this is a prop rather than
 * the picker simply omitting the control. Every app in the house keeps the
 * mark in the same corner; on the picker itself it would be a link to the page
 * you are already on - the kind of control that teaches people their click did
 * nothing. So the mark stays, at rest, and is not a target.
 *
 * In that state it is decoration and is hidden from assistive technology. The
 * alternative - a span carrying `aria-current="page"` and the "All apps" name -
 * announces "all apps, current page" on the page that *is* all apps, which is
 * a sentence that costs a screen-reader user time and tells them nothing the
 * heading below does not.
 */
export function AppSwitcher({ href = '/', label = 'All apps', current = false, className }: AppSwitcherProps) {
  const classes = ['aerie-app-switcher'];
  if (current) classes.push('aerie-app-switcher--current');
  if (className) classes.push(className);

  if (current) {
    return (
      <span className={classes.join(' ')} aria-hidden="true">
        <AppsMark />
      </span>
    );
  }

  return (
    <a className={classes.join(' ')} href={href} aria-label={label} title={label}>
      <AppsMark />
    </a>
  );
}

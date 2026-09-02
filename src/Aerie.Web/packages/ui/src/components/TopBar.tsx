import type { ReactNode } from 'react';
import { AppSwitcher } from './AppSwitcher';
import { ThemeSwitch } from './ThemeSwitch';
import './TopBar.css';

export interface TopBarProps {
  /** The app's own name, and the only text the bar states. Not a tagline: the
      bar says where you are, and the page below says what is on it. */
  appName: string;
  /** Passed through to the <AppSwitcher>. */
  homeHref?: string;
  /** Set by the app picker itself, where the switcher slot is the home state
      rather than a link to the page you are already on. */
  atHome?: boolean;
  /** App-supplied content, after the app name. An environment badge, a
      breadcrumb, a search box - whatever the app needs the bar to carry. */
  leading?: ReactNode;
  /** App-supplied content, before the theme switch. Account menus, actions. */
  trailing?: ReactNode;
  className?: string;
}

/**
 * The bar every Aerie app wears. Admin, the gallery and the app picker render
 * it today; auth, docs, modeler and family are why it is in @aerie/ui rather
 * than in any one of them.
 *
 * Three things it deliberately is not:
 *
 * - **Not tall.** It replaced a 32px-padded block with a 32px title and a
 *   subtitle - about 131px of chrome above every page. The bar is 48px: one
 *   row, the height of the control in it. Chrome is not the product.
 * - **Not a page heading.** The app name is a wordmark and renders as a
 *   <span>, so the <h1> stays where the page's own heading is. The <header>
 *   element is the banner landmark; that is the semantics identity needs.
 * - **Not extensible by forking.** An app that needs more in the bar passes
 *   `leading`/`trailing`. Nothing app-specific compiles into this file.
 *
 * The theme control is always present and always last, so it is in the same
 * place in every app. That makes a <ThemeProvider> above this component a
 * requirement, not a nicety - useTheme throws without one, which is the loud
 * failure a silently-light toggle is not.
 *
 * **It has a sibling**: `@aerie/ui/standalone/topbar` renders the same bar with
 * DOM calls for the two pages that are not React apps - Swagger UI and the
 * logo export. It imports this file's stylesheet rather than restating it, so
 * a change to TopBar.css reaches both; a change to the *markup* here has to be
 * made there too. That file carries the rule, and the list of what differs.
 */
export function TopBar({ appName, homeHref, atHome, leading, trailing, className }: TopBarProps) {
  return (
    <header className={className ? `aerie-topbar ${className}` : 'aerie-topbar'}>
      <div className="aerie-topbar__inner">
        <div className="aerie-topbar__side">
          <AppSwitcher href={homeHref} current={atHome} />
          <span className="aerie-topbar__name">{appName}</span>
          {leading}
        </div>
        <div className="aerie-topbar__side aerie-topbar__side--end">
          {trailing}
          <ThemeSwitch tone="accent" />
        </div>
      </div>
    </header>
  );
}

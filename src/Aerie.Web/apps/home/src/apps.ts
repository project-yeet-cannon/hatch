/**
 * Everything the house runs, in the order it is offered.
 *
 * One list, and nothing else in the app enumerates the apps: adding a service
 * is an entry here. The three tiers are the sentence the picker is making -
 * what a family member opens, what the house is configured with, and what is
 * watching the machines - so they carry their own line rather than being three
 * unlabelled runs of tiles.
 *
 * The icons are emoji, carried across from the static page this app replaced.
 * They are placeholders in the same sense every other value here is: what
 * Aerie's iconography should be is Phase 6's to answer (docs/plans/
 * design-system-mvp.md), and a set drawn now would be a set drawn twice.
 */

export interface AppEntry {
  name: string;
  description: string;
  /** Decoration. The name beside it is the accessible name of the tile. */
  icon: string;
  /** A same-origin path. Exactly one of `href` and `subdomain` is set. */
  href?: string;
  /** A sibling service on its own subdomain, resolved at render against the
      domain this page is served from - see lib/siblingOrigin.ts. */
  subdomain?: string;
  /** Hidden outright when its bundle answers 404 - the operator's own apps,
      which are withheld from everyone else. See lib/useWithheldApps.ts.

      Not set for an app this pod does not serve the bundle of. Trading is one:
      its SPA is served by the trading service on its own host, so there is no
      same-origin path to probe, and a cross-origin HEAD would answer for the
      wrong reasons. It is listed unconditionally, like the observability
      services below it, and the wall in front of that host is what decides who
      gets in. */
  withheldIf404?: boolean;
  /** The same-origin path to HEAD when deciding that, for an app whose own
      address this page cannot probe. `href` is the probe when there is one;
      Hatch needs this because it lives on its own subdomain while its bundle
      is served by the same pod as this page, and a cross-origin HEAD would
      answer for the wrong reasons. */
  probePath?: string;
}

export interface Tier {
  name: string;
  description: string;
  apps: AppEntry[];
}

export const TIERS: Tier[] = [
  {
    name: 'Family',
    description: 'Everyday apps, on any enrolled device',
    apps: [
      {
        name: 'Kiosk Dashboard',
        description: 'Live zone temps & outside weather',
        icon: '🏠',
        href: '/apps/dashboard/',
      },
      {
        name: 'Family',
        description: 'Household apps — add to your home screen',
        icon: '📦',
        href: '/apps/family/',
      },
      {
        name: 'Files',
        description: 'Browse & upload files on the house share',
        icon: '📁',
        subdomain: 'share',
      },
    ],
  },
  {
    name: 'Operations',
    description: 'Configuring, documenting and extending the house',
    apps: [
      {
        name: 'Admin',
        description: 'System configuration & data management',
        icon: '⚙️',
        href: '/apps/admin/',
        withheldIf404: true,
      },
      {
        name: 'Hatch',
        description: 'The house project tracker — plans, issues, the board',
        icon: '🐣',
        subdomain: 'hatch',
        withheldIf404: true,
        probePath: '/apps/hatch/',
      },
      {
        name: 'Trading',
        description: 'Strategies, sweeps and the leaderboard',
        icon: '📊',
        subdomain: 'trading',
      },
      {
        name: 'Docs',
        description: 'Browse the architecture docs',
        icon: '📚',
        href: '/apps/docs/',
      },
      {
        name: 'API',
        description: 'Swagger docs & API explorer',
        icon: '📄',
        href: '/swagger',
      },
      {
        name: 'Modeler',
        description: 'Sketch the home in 3D for CFD/CAD export',
        icon: '🏗️',
        href: '/apps/modeler/',
      },
      {
        name: 'Design',
        description: 'The shared design system, token by token',
        icon: '🎨',
        href: '/apps/design/',
      },
      {
        name: 'Logo',
        description: 'Parametric bird mark & wordmark',
        icon: '🐦',
        href: '/apps/logo/',
      },
    ],
  },
  /* The observability tier. Every entry is a service Aerie operates but did not
     write, on its own subdomain and behind its own login - these hosts
     deliberately do not carry the auth middleware (docs/plans/auth.md, "The
     allow-list is load-bearing"), so the sign-in that covers the two tiers
     above does not cover these. */
  {
    name: 'Infrastructure',
    description: 'Monitoring stack — each keeps its own login',
    apps: [
      {
        name: 'Status',
        description: 'Uptime Kuma — is everything up?',
        icon: '🩺',
        subdomain: 'status',
      },
      {
        name: 'Metrics',
        description: 'Grafana dashboards over Prometheus',
        icon: '📈',
        subdomain: 'metrics',
      },
      {
        name: 'Logs',
        description: 'OpenSearch Dashboards — search all logs',
        icon: '🔎',
        subdomain: 'logs',
      },
    ],
  },
];

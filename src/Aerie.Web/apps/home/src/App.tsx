import { Badge, Card, Grid, PageHeader, Text, TopBar } from '@aerie/ui';
import './App.css';
import { TIERS, type AppEntry } from './apps';
import { siblingOrigin } from './lib/siblingOrigin';
import { useWithheldApps } from './lib/useWithheldApps';

/**
 * One tile: an icon, the app's name as the link, and a line saying what it is.
 *
 * The whole card is the hit target, but only the name is the link - its
 * `::after` in App.css stretches over the card. The old page wrapped the
 * anchor around all four pieces, which made every tile announce itself as
 * "🏠 Kiosk Dashboard Live zone temps & outside weather ›". The accessible
 * name is the app's name now, and the rest is what it always was: decoration
 * and a description.
 *
 * `href` is null for a sibling service this page cannot address - see
 * lib/siblingOrigin.ts. The tile stays, without a link and saying why. The
 * service is there; the way to it from here is not.
 */
function Tile({ app, href }: { app: AppEntry; href: string | null }) {
  return (
    <Card className={href ? 'home-tile' : 'home-tile home-tile--unreachable'}>
      <span className="home-tile__icon" aria-hidden="true">
        {app.icon}
      </span>
      <span className="home-tile__body">
        {href ? (
          <a className="home-tile__link" href={href}>
            {app.name}
          </a>
        ) : (
          <span className="home-tile__name">{app.name}</span>
        )}
        <Text as="span" tone="muted" className="home-tile__desc">
          {app.description}
        </Text>
      </span>
      {href ? (
        /* Set in the bundled face rather than left to whatever the machine
           resolved, which is what made the old page's ▦ app-switcher glyph
           worth replacing with a drawing. This one is punctuation doing a
           pointer's job in a face that is now guaranteed, and it is
           decoration - the link beside it is the control. */
        <span className="home-tile__chev" aria-hidden="true">
          ›
        </span>
      ) : (
        <Badge>No domain</Badge>
      )}
    </Card>
  );
}

/**
 * The app picker: every service in the house, in three tiers.
 *
 * It wears the shared bar like every other app, with one difference - the
 * app-switcher is in its home state rather than a link, because this is the
 * page that control goes to.
 */
export function App() {
  const withheld = useWithheldApps();

  return (
    <div className="home-app">
      <TopBar appName="Aerie" atHome />

      <main className="home-content">
        <PageHeader title="Apps" description="Home citadel" />

        {TIERS.map((tier) => {
          const apps = tier.apps.filter((app) => !withheld.has(app.name));
          /* A tier whose every app is withheld renders nothing rather than an
             empty heading. There is no <EmptyState> here on purpose: an empty
             state says "nothing is here", and what is true is that nothing
             here is yours - which is the sentence the 404 exists to avoid
             saying. */
          if (apps.length === 0) return null;

          return (
            <section className="home-tier" key={tier.name}>
              <div className="home-tier-head">
                <h2 className="home-tier-name">{tier.name}</h2>
                <Text tone="muted" className="home-tier-desc">
                  {tier.description}
                </Text>
              </div>

              <Grid cols={3}>
                {apps.map((app) => (
                  <Tile
                    key={app.name}
                    app={app}
                    href={app.subdomain ? siblingOrigin(location.hostname, app.subdomain) : (app.href ?? null)}
                  />
                ))}
              </Grid>
            </section>
          );
        })}

        <footer className="home-footer">❤️</footer>
      </main>
    </div>
  );
}

import { AppSwitcher, ThemeSwitch, TopBar } from '@aerie/ui';
import { GalleryPage, GallerySection } from '../components/Gallery';

/* Every bar on this page is the real component, wired to the real theme: click
   a specimen's Light and the whole gallery goes light, because there is one
   theme and this is its control. The app-switcher links really do leave for the
   picker, for the same reason - a specimen that mocks the behaviour is a
   specimen that can be wrong about it.

   The <header> each <TopBar> renders is nested inside this page's <article>,
   so none of them is a second banner landmark. Only the app's own bar, above
   the page, is one. */

export function TopBarPage() {
  return (
    <GalleryPage
      title="Top bar"
      blurb="The bar every Aerie app wears: where you are, the way back to the other apps, and the theme. One row, 48px, and nothing else."
    >
      <GallerySection
        title="As it ships"
        note="What admin renders. The app name and nothing else - the bar says where you are, and the page below says what is on it. It replaces a 131px gradient header carrying a 32px title and a subtitle: 63% of the chrome above every admin page, given back to the page."
      >
        <div className="stage">
          <TopBar appName="Aerie Admin" />
          <div className="stage-page">
            <h3>Zones</h3>
            <p className="gallery-note">The page starts here.</p>
          </div>
        </div>
      </GallerySection>

      <GallerySection
        title="Slots"
        note="An app adds to the bar rather than forking it: the leading slot sits after the app name, the trailing slot before the theme control. The theme control is always last, so it is in the same place in every app."
      >
        <div className="stage">
          <TopBar
            appName="Aerie Docs"
            leading={<span className="stage-chip">Staging</span>}
            trailing={
              <button type="button" className="gallery-button">
                Sign out
              </button>
            }
          />
        </div>
      </GallerySection>

      <GallerySection
        title="A narrow window, and a long name"
        note="The name gives way first: it ellipses, while the app switcher and the theme control keep their size. A target that shrank to make room for a title would be the wrong thing to shrink."
      >
        <div className="stage stage--narrow">
          <TopBar appName="Aerie Provisioning Console" />
        </div>
      </GallerySection>

      <GallerySection
        title="The app switcher"
        note="A 36px target with an accessible name, in place of the ▦ character admin used to render as a link. It is an <a>, so middle-click and copy-link-address work. Tab to it to see the focus ring, which is drawn in --on-accent because a --primary ring on the --primary fill would be invisible."
      >
        <div className="switcher-ground">
          <AppSwitcher />
          <AppSwitcher label="All apps (custom label)" />
        </div>
      </GallerySection>

      <GallerySection
        title="The theme control, on both grounds"
        note="One component, two grounds. On a surface the inactive options are --muted; on the bar every label is full --on-accent and the filled pill alone carries the state, because dimming 12px type on the primary fill lands at about 3.5:1."
      >
        <div className="tone-row">
          <div className="tone-sample">
            <ThemeSwitch tone="surface" />
            <span className="swatch-use">tone="surface" — a page or a card</span>
          </div>
          <div className="tone-sample switcher-ground">
            <ThemeSwitch tone="accent" />
            <span className="swatch-use">tone="accent" — a filled bar</span>
          </div>
        </div>
      </GallerySection>
    </GalleryPage>
  );
}

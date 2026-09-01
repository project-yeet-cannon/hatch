import { useState } from 'react';
import { GalleryPage, GallerySection, TokenName, TokenValue } from '../components/Gallery';
import { useTokenValues } from '../lib/useTokenValues';

const ALL_TOKENS = ['--shadow', '--overlay', '--transition'];

export function ElevationPage() {
  const values = useTokenValues(ALL_TOKENS);
  const [scrimUp, setScrimUp] = useState(false);

  return (
    <GalleryPage
      title="Elevation & motion"
      blurb="One shadow, one scrim, one duration. Calm is the rule inherited from the dashboard: nothing moves unless a person moved it."
    >
      <GallerySection
        title="Shadow"
        note="One step, not a ramp. Two surfaces are as many as this system stacks - a card on the page, and a modal on the scrim - so a second elevation would be a distinction with nothing to distinguish."
      >
        <div className="elevation-row">
          <div className="elevation-card">A card on the page</div>
          <div className="specimen-meta">
            <TokenName name="--shadow" />
            <TokenValue value={values['--shadow']} />
            <span className="swatch-use">Cards, modals, the checked segment of a switch</span>
          </div>
        </div>
      </GallerySection>

      <GallerySection
        title="Scrim"
        note="What a modal lays over the page. It is deeper in dark than in light, because a translucent black over a dark ground separates less than the same black over a light one."
      >
        <div className="scrim-stage">
          <div className="scrim-content">
            <p>Zone thermostat · 21.4 °C</p>
            <p className="gallery-note">Kitchen · Hallway · Garage</p>
          </div>
          {scrimUp ? (
            <div className="scrim">
              <div className="scrim-panel">A panel over the scrim</div>
            </div>
          ) : null}
        </div>
        <div className="specimen-meta">
          <button type="button" className="gallery-button" onClick={() => setScrimUp((up) => !up)}>
            {scrimUp ? 'Lower the scrim' : 'Raise the scrim'}
          </button>
          <TokenName name="--overlay" />
          <TokenValue value={values['--overlay']} />
        </div>
      </GallerySection>

      <GallerySection
        title="Motion"
        note="One duration and one easing for every state change in the system. Hover the block: this is the whole motion vocabulary, and a transition written as a number anywhere else is a bug."
      >
        <div className="elevation-row">
          <div className="motion-block">Hover me</div>
          <div className="specimen-meta">
            <TokenName name="--transition" />
            <TokenValue value={values['--transition']} />
            <span className="swatch-use">Color, background and border changes</span>
          </div>
        </div>
      </GallerySection>
    </GalleryPage>
  );
}

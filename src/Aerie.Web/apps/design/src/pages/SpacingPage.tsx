import { GalleryPage, GallerySection, TokenName, TokenValue } from '../components/Gallery';
import { useTokenValues } from '../lib/useTokenValues';

const LADDER = [
  { name: '--sp-1', use: 'Icon to its label' },
  { name: '--sp-1-5', use: 'Half-step: a compact row that is cramped at 8 and loose at 12' },
  { name: '--sp-2', use: 'Inside a control; between chips' },
  { name: '--sp-2-5', use: 'Half-step: a table cell’s vertical padding' },
  { name: '--sp-3', use: 'Between rows in a list' },
  { name: '--sp-4', use: 'A card’s padding; between cards in a grid' },
  { name: '--sp-5', use: 'A page’s outer padding' },
  { name: '--sp-6', use: 'Between sections' },
  { name: '--sp-8', use: 'Between a page’s major bands' },
  { name: '--sp-10', use: 'The widest gap the ladder offers' },
];

const LADDER_TOKENS = LADDER.map((rung) => rung.name);
const MEASURE = '--measure';
const ALL_TOKENS = [...LADDER_TOKENS, MEASURE];

export function SpacingPage() {
  const values = useTokenValues(ALL_TOKENS);

  return (
    <GalleryPage
      title="Spacing"
      blurb="One base of 4 and one ladder. Below the base, hairlines and one-to-three-pixel optical nudges are free."
    >
      <GallerySection
        title="The ladder"
        note="Space between things. The fixed dimensions of an ornament - a 40px avatar, a 64×48 album cover - stay literal at their use site: they are sized to their content, and retuning this ladder must not resize them by accident."
      >
        <div className="ladder">
          {LADDER.map((rung) => (
            <div key={rung.name} className="ladder-rung">
              <div className="ladder-bar" style={{ width: `var(${rung.name})` }} />
              <TokenName name={rung.name} />
              <TokenValue value={values[rung.name]} />
              <span className="swatch-use">{rung.use}</span>
            </div>
          ))}
        </div>
      </GallerySection>

      <GallerySection
        title="The two half-steps, in place"
        note="Why they exist. The same six-row list at --sp-2, at --sp-2-5 and at --sp-3: the first is cramped, the last stops being scannable."
      >
        <div className="density-row">
          {['--sp-2', '--sp-2-5', '--sp-3'].map((token) => (
            <div key={token} className="density-sample">
              <div className="density-list">
                {['Living Room', 'Kitchen', 'Hallway', 'Garage', 'Study', 'Landing'].map((label) => (
                  <div key={label} className="density-item" style={{ paddingBlock: `var(${token})` }}>
                    {label}
                  </div>
                ))}
              </div>
              <div className="specimen-meta">
                <TokenName name={token} />
                <TokenValue value={values[token]} />
              </div>
            </div>
          ))}
        </div>
      </GallerySection>

      <GallerySection
        title="The measure"
        note="Every page's content column is capped here, so a wide monitor gets margins rather than a line of text nobody can track back from."
      >
        <div className="measure-demo">
          {/* Drawn at its true width inside a track that clips: when the bar
              fills the track edge to edge, this window is narrower than the
              measure and the cap is not what is limiting the column. That is
              the thing worth seeing, and a scaled-down bar would hide it. */}
          <div className="measure-track">
            <div className="measure-bar" />
          </div>
          <div className="specimen-meta">
            <TokenName name={MEASURE} />
            <TokenValue value={values[MEASURE]} />
            <span className="swatch-use">The reading measure</span>
          </div>
        </div>
      </GallerySection>
    </GalleryPage>
  );
}

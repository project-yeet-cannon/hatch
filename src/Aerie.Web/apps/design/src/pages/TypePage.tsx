import { GalleryPage, GallerySection, TokenName, TokenValue } from '../components/Gallery';
import { useTokenValues } from '../lib/useTokenValues';

/* One string at every size, so the registers can be compared rather than just
   listed. Deliberately generic - a room type, not a room (docs/ethos.md). */
const SPECIMEN = 'Zone thermostat · 21.4 °C';

const REGISTERS = [
  { name: '--t-display', use: 'The app title, an invite code' },
  { name: '--t-title', use: 'h1' },
  { name: '--t-heading', use: 'h2' },
  { name: '--t-subhead', use: 'h3' },
  { name: '--t-section', use: 'h4' },
  { name: '--t-meta', use: 'Set above body inside a fixed ornament - avatar initials' },
  { name: '--t-body', use: 'The base size: h5, buttons, inputs, table cells' },
  { name: '--t-label', use: 'h6, field labels, badges, table headers' },
  { name: '--t-micro', use: 'Chart axes, legends, tooltips' },
];

const FACES = [
  { name: '--ui', use: 'Everything. Manrope Variable, self-hosted - no request leaves the house for it.' },
  { name: '--mono', use: 'Identifiers, codes, values that must line up' },
];

const LEADING = [
  { name: '--lh-tight', use: 'Headings, and any line that will not wrap twice' },
  { name: '--lh-body', use: 'Prose and table cells' },
];

const ALL_TOKENS = [...REGISTERS, ...FACES, ...LEADING].map((token) => token.name);

export function TypePage() {
  const values = useTokenValues(ALL_TOKENS);

  return (
    <GalleryPage
      title="Type"
      blurb="Named registers, not sizes. Admin is a data-dense CRUD app read at desk distance, so these are its own existing sizes given names - the dashboard's arm's-length register is deliberately not copied across."
    >
      <GallerySection
        title="Registers"
        note="A size outside this table is a bug. The one licensed exception is a glyph scaled to fit a fixed box, which is an ornament rather than text."
      >
        <div className="specimen-list">
          {REGISTERS.map((register) => (
            <div key={register.name} className="specimen">
              <div className="specimen-line" style={{ fontSize: `var(${register.name})` }}>
                {SPECIMEN}
              </div>
              <div className="specimen-meta">
                <TokenName name={register.name} />
                <TokenValue value={values[register.name]} />
                <span className="swatch-use">{register.use}</span>
              </div>
            </div>
          ))}
        </div>
      </GallerySection>

      <GallerySection title="Faces">
        <div className="specimen-list">
          {FACES.map((face) => (
            <div key={face.name} className="specimen">
              <div className="specimen-line specimen-face" style={{ fontFamily: `var(${face.name})` }}>
                {SPECIMEN}
              </div>
              <div className="specimen-meta">
                <TokenName name={face.name} />
                <TokenValue value={values[face.name]} />
                <span className="swatch-use">{face.use}</span>
              </div>
            </div>
          ))}
        </div>
      </GallerySection>

      <GallerySection
        title="Leading"
        note="Two values, because a heading and a paragraph want different air and nothing in between has come up."
      >
        <div className="specimen-list">
          {LEADING.map((leading) => (
            <div key={leading.name} className="specimen">
              <p className="specimen-paragraph" style={{ lineHeight: `var(${leading.name})` }}>
                {SPECIMEN} — a zone reports the temperature it last saw, the time it saw it, and nothing
                else. Two sentences is enough to see where the lines sit.
              </p>
              <div className="specimen-meta">
                <TokenName name={leading.name} />
                <TokenValue value={values[leading.name]} />
                <span className="swatch-use">{leading.use}</span>
              </div>
            </div>
          ))}
        </div>
      </GallerySection>
    </GalleryPage>
  );
}

import { GalleryPage, GallerySection, TokenName } from '../components/Gallery';
import { Swatch } from '../components/Swatch';
import { useTokenValues } from '../lib/useTokenValues';

const SERIES = ['--series-1', '--series-2', '--series-3', '--series-4', '--series-5', '--series-6', '--series-7', '--series-8'];

/* Generic channel names. A chart in the gallery must not name a real room, a
   real device or a real person (docs/ethos.md). */
const CHANNELS = ['Channel A', 'Channel B', 'Channel C', 'Channel D', 'Channel E', 'Channel F', 'Channel G', 'Channel H'];

export function SeriesPage() {
  const values = useTokenValues(SERIES);

  return (
    <GalleryPage
      title="Series"
      blurb="The categorical ramp, in fixed order. It is validated for colour-vision-deficiency safety as a sequence, so a chart takes --series-1 first, then --series-2, and never picks from the middle."
    >
      <GallerySection
        title="The ramp"
        note="Not a value to improvise on. Adding a ninth colour, reordering these eight, or substituting one for a brand colour breaks the property the ramp was chosen for."
      >
        <div className="swatch-grid">
          {SERIES.map((token, index) => (
            <Swatch key={token} token={token} value={values[token]} use={CHANNELS[index]} />
          ))}
        </div>
      </GallerySection>

      <GallerySection
        title="Adjacent"
        note="The context that matters: a state timeline puts these bands edge to edge with no gap and no label between them. If two are hard to tell apart, this is where it shows and a swatch grid would not."
      >
        <div className="series-timeline">
          {SERIES.map((token, index) => (
            <div key={token} className="series-band" style={{ background: `var(${token})` }}>
              <span className="series-band-label">{index + 1}</span>
            </div>
          ))}
        </div>
      </GallerySection>

      <GallerySection
        title="As lines"
        note="The same eight as strokes rather than fills. A hue that separates well as a wide band can collapse at two pixels, so both readings are worth having."
      >
        <div className="series-lines">
          {SERIES.map((token, index) => (
            <div key={token} className="series-line-row">
              <span className="series-line-swatch" style={{ background: `var(${token})` }} />
              <span className="series-line-rule" style={{ background: `var(${token})` }} />
              <TokenName name={token} />
              <span className="swatch-use">{CHANNELS[index]}</span>
            </div>
          ))}
        </div>
      </GallerySection>
    </GalleryPage>
  );
}

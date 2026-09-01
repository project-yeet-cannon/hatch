import { GalleryPage, GallerySection, TokenName, TokenValue } from '../components/Gallery';
import { useTokenValues } from '../lib/useTokenValues';

const STEPS = [
  { name: '--r', use: 'A card, or any card-sized surface' },
  { name: '--r-ctl', use: 'Anything a pointer presses: buttons, inputs, tappable rows' },
  { name: '--r-in', use: 'A content block nested inside a card' },
  { name: '--r-chip', use: 'The smallest chips and marks' },
];

const PILL = '--r-pill';
const ALL_TOKENS = [...STEPS.map((step) => step.name), PILL];

export function RadiusPage() {
  const values = useTokenValues(ALL_TOKENS);

  return (
    <GalleryPage
      title="Radius"
      blurb="Four steps and a pill, stated once and used everywhere. The four-step shape is the dashboard's; the numbers are admin's, carried across unchanged."
    >
      <GallerySection
        title="The scale"
        note="A radius stated as a number is a bug. It is a value a designer cannot find, and it is the value that makes one card in one corner of the app look like it came from somewhere else."
      >
        <div className="radius-row">
          {STEPS.map((step) => (
            <div key={step.name} className="radius-step">
              <div className="radius-box" style={{ borderRadius: `var(${step.name})` }} />
              <TokenName name={step.name} />
              <TokenValue value={values[step.name]} />
              <span className="swatch-use">{step.use}</span>
            </div>
          ))}
        </div>
      </GallerySection>

      <GallerySection
        title="Fully round"
        note="A pill rather than a fifth step, because a badge's radius should not change when its height does. A circle stays 50% at its use site: that is a shape, not a step on this scale."
      >
        <div className="radius-row">
          <div className="radius-step">
            <div className="radius-pill" style={{ borderRadius: `var(${PILL})` }}>
              Online
            </div>
            <TokenName name={PILL} />
            <TokenValue value={values[PILL]} />
            <span className="swatch-use">Badges and status chips</span>
          </div>
        </div>
      </GallerySection>

      <GallerySection
        title="Nested"
        note="The steps in the relationship they were drawn for: a control and an inset block inside a card. Each inner radius is smaller than the one containing it, which is what keeps the corners concentric."
      >
        <div className="radius-nest" style={{ borderRadius: 'var(--r)' }}>
          <div className="radius-nest-control" style={{ borderRadius: 'var(--r-ctl)' }}>
            Control
          </div>
          <div className="radius-nest-inset" style={{ borderRadius: 'var(--r-in)' }}>
            An inset block
          </div>
        </div>
      </GallerySection>
    </GalleryPage>
  );
}

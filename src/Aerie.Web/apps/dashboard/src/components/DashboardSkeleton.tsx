interface SkelProps {
  width: number | string;
  height: number;
  style?: React.CSSProperties;
}

function Skel({ width, height, style }: SkelProps) {
  return <span className="hf-skel" style={{ width, height, ...style }} />;
}

// Mirrors the ClimateCard's DOM shape - tab row, rule, pane - so the real
// card slots in without shifting layout once data arrives.
function ClimateCardSkeleton() {
  return (
    <div className="hf-climate">
      <div className="hf-ctabs">
        {Array.from({ length: 3 }, (_, i) => (
          <div key={i} className="hf-ctab" style={{ cursor: 'default' }}>
            <span className="hf-ctab-name">
              <span className="hf-swatch" style={{ background: 'var(--skel)' }} />
              <Skel width={72} height={15} />
            </span>
            <Skel width={40} height={22} />
          </div>
        ))}
      </div>
      <div className="hf-climate-rule" aria-hidden="true" />
      <div className="hf-cpane-head">
        <Skel width={92} height={44} />
        <Skel width="40%" height={15} />
      </div>
      <span className="hf-skel hf-chart" style={{ height: 110, display: 'block' }} />
      <span className="hf-skel hf-xax" />
      <div className="hf-foot">
        <Skel width={92} height={13} />
        <Skel width={78} height={13} />
        <Skel width={100} height={13} />
      </div>
    </div>
  );
}

// The stage's box with nothing to show yet - the same fixed shape, so the
// column doesn't jump when the first photo or agenda row lands.
function StageSkeleton() {
  return (
    <div className="hf-stage">
      <span className="hf-skel" style={{ position: 'absolute', inset: 0, borderRadius: 0 }} />
    </div>
  );
}

/**
 * Placeholder for page one while the initial `/api/dashboard` fetch is in
 * flight, so the page paints immediately instead of staying blank for the
 * few seconds the request takes. Page one's shapes only - page two is below
 * the fold by definition, and a skeleton nobody can see is noise.
 */
export function DashboardSkeleton() {
  return (
    <>
      <div className="hf-zones">
        <ClimateCardSkeleton />
      </div>
      <StageSkeleton />
    </>
  );
}

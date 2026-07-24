interface SkelProps {
  width: number | string;
  height: number;
  style?: React.CSSProperties;
}

function Skel({ width, height, style }: SkelProps) {
  return <span className="hf-skel" style={{ width, height, ...style }} />;
}

// Mirrors OutsideCard's DOM shape so the real card slots in without shifting
// layout once data arrives.
function OutsideCardSkeleton() {
  return (
    <div className="hf-out">
      <div className="hf-brow" style={{ marginBottom: 12 }}>
        <span className="hf-swatch" style={{ background: 'var(--skel)' }} />
        <span className="hf-name" style={{ width: 'auto' }}>
          Outside
        </span>
        <Skel width={44} height={25} style={{ marginLeft: 'auto' }} />
      </div>
      <span className="hf-skel hf-chart" style={{ height: 130, display: 'block' }} />
      <span className="hf-skel hf-xax" />
      <div className="hf-foot">
        <Skel width={92} height={13} />
        <Skel width={78} height={13} />
        <Skel width={100} height={13} />
      </div>
      <Skel width="70%" height={13} style={{ marginTop: 11 }} />
    </div>
  );
}

// Mirrors ZoneCard's collapsed <summary> row.
function ZoneCardSkeleton() {
  return (
    <div className="hf-zone">
      <div className="hf-zsum">
        <span className="hf-swatch" style={{ background: 'var(--skel)' }} />
        <Skel width={100} height={19} />
        <span className="hf-skel hf-spark" />
        <Skel width={38} height={25} />
        <Skel width={64} height={26} style={{ borderRadius: 30 }} />
        <span className="hf-chev" style={{ visibility: 'hidden' }}>
          ›
        </span>
      </div>
    </div>
  );
}

/**
 * Placeholder shown in place of the zone/outside cards while the initial
 * `/api/dashboard` fetch is in flight, so the page paints immediately
 * instead of staying blank for the few seconds the request takes.
 */
export function DashboardSkeleton({ zoneCount = 3 }: { zoneCount?: number }) {
  return (
    <div className="hf-zones">
      <OutsideCardSkeleton />
      {Array.from({ length: zoneCount }, (_, i) => (
        <ZoneCardSkeleton key={i} />
      ))}
    </div>
  );
}

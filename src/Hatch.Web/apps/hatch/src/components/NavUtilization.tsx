import { useEffect, useState } from 'react';
import { useUtilization } from '../lib/useUtilization';
import { hasBattery } from '../lib/utilization';
import { UtilizationBattery } from './UtilizationBattery';
import { UtilizationModal } from './UtilizationModal';

/** How often the phrases are re-read. The reading itself is polled far less
    often; this is only so `resets in 1h 20m` does not sit at 1h 20m for two
    minutes while the ring beside it says otherwise. */
const TICK_MS = 30 * 1000;

/**
 * The battery and its modal, and the one place they meet.
 *
 * Its own component rather than four more lines in App.tsx, because there are
 * three pieces of state here - the reading, whether the modal is open, and a
 * clock - and only the first of them is App's business. App mounts one element
 * and the nav strip stays a list of links.
 *
 * The reading is held here and passed to both, which is what makes the modal
 * free to open (it renders what the nav already has) and what makes the refresh
 * inside it move the glyph in the strip: one piece of state, not two.
 */
export function NavUtilization() {
  const { reading, refresh } = useUtilization();
  const [open, setOpen] = useState(false);
  const [now, setNow] = useState(() => new Date());

  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), TICK_MS);
    return () => clearInterval(timer);
  }, []);

  // The 204: no token configured on this installation. Nothing renders at all -
  // not the modal either, which is why this sits above both rather than inside
  // the battery alone.
  if (!hasBattery(reading)) return null;

  return (
    <>
      <UtilizationBattery reading={reading} now={now} onOpen={() => setOpen(true)} />
      <UtilizationModal
        open={open}
        onClose={() => setOpen(false)}
        reading={reading}
        now={now}
        onRefresh={refresh}
      />
    </>
  );
}

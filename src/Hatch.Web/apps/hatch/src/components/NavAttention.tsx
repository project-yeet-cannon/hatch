import { useEffect, useRef, useState } from 'react';
import { attentionLabel, attentionTone } from '../lib/attention';
import { useAttention } from '../lib/useAttention';
import { AttentionPanel } from './AttentionPanel';

/** How often the waiting phrases are re-read, so `4 minutes` does not sit at
    `4 minutes` for a quarter of an hour while the panel is open. The answer
    itself is polled separately - see `useAttention`. */
const TICK_MS = 30 * 1000;

/** The glyph's own units. A 24-unit box, matching the battery beside it. */
const SIZE = 24;

/**
 * Whether the loop is waiting on a person, at the right end of the nav strip.
 *
 * Quiet while it is not: muted ink, no fill, no border, a glyph and one word.
 * Loud the moment it is: the glyph lights, and one count pill per non-empty
 * section appears. It measures the same in both states, because a widget that
 * grew when something arrived would shove the battery leftwards and move the
 * strip out from under the cursor.
 *
 * Never returns null. Unlike the battery - which draws nothing on an install
 * with no Claude token, because there is no such thing as its answer there -
 * "nothing is waiting" is an answer worth drawing, and it is most of the time.
 *
 * A dropdown rather than `@hatch/ui`'s Modal: the panel is a short list of
 * links anchored to the thing that opened it, and a modal's overlay would dim
 * the board behind a list of links back into it.
 */
export function NavAttention() {
  const attention = useAttention();
  const [open, setOpen] = useState(false);
  const [now, setNow] = useState(() => new Date());
  const container = useRef<HTMLDivElement>(null);

  const tone = attentionTone(attention);
  const label = attentionLabel(attention);
  const reviews = attention?.reviews.length ?? 0;
  const questions = attention?.questions.length ?? 0;

  useEffect(() => {
    if (!open) return;
    const timer = setInterval(() => setNow(new Date()), TICK_MS);
    return () => clearInterval(timer);
  }, [open]);

  /* A pointer outside it closes it. Clicking a non-focusable area of the page
     fires no blur at all, which is why this exists as well as the focusout
     below - the same pair IssuePicker uses. */
  useEffect(() => {
    if (!open) return;
    const outside = (e: PointerEvent) => {
      if (!container.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('pointerdown', outside);
    return () => document.removeEventListener('pointerdown', outside);
  }, [open]);

  return (
    <div
      className="hatch-attention"
      ref={container}
      /* React's onBlur is focusout, so it bubbles: Tab out of the last link in
         the panel moves focus onward natively and the panel goes with it, with
         no Tab handling and no preventDefault anywhere near it. */
      onBlur={(e) => {
        if (!e.currentTarget.contains(e.relatedTarget)) setOpen(false);
      }}
      onKeyDown={(e) => {
        if (e.key !== 'Escape' || !open) return;
        e.preventDefault();
        setOpen(false);
        // Back to what opened it, rather than nowhere: Escape from inside a
        // list of links should not cost somebody their place in the page.
        container.current?.querySelector('button')?.focus();
      }}
    >
      <button
        type="button"
        className={`hatch-attention-control hatch-attention-${tone}`}
        onClick={() => setOpen((was) => !was)}
        title={label}
        aria-label={label}
        aria-haspopup="true"
        aria-expanded={open}
      >
        <Robot lit={tone === 'asking'} />

        {tone === 'rest' ? (
          <span className="hatch-attention-word">Clear</span>
        ) : (
          <span className="hatch-attention-pills">
            {/* A digit in each pill, not a dot: the loud state has to be
                legible without colour, and the accessible name above says the
                same thing in words. A pill per non-empty section, so an empty
                half takes no room rather than showing a zero. */}
            {reviews > 0 && <span className="hatch-attention-pill hatch-attention-pill-review">{reviews}</span>}
            {questions > 0 && <span className="hatch-attention-pill hatch-attention-pill-question">{questions}</span>}
          </span>
        )}
      </button>

      {open && <AttentionPanel attention={attention} now={now} />}
    </div>
  );
}

/**
 * A robot head with an antenna, inline the way the battery's glyph is.
 *
 * The whole thing is muted at rest with the antenna's lamp unlit. When
 * something is waiting the lamp and the eyes light, and the lamp is the only
 * part that moves - the motion is confined to it, and there is none at all
 * under `prefers-reduced-motion: reduce` (App.css).
 */
function Robot({ lit }: { lit: boolean }) {
  return (
    <svg
      className="hatch-attention-glyph"
      viewBox={`0 0 ${SIZE} ${SIZE}`}
      aria-hidden="true"
      focusable="false"
    >
      {/* The antenna: a stalk up out of the head, and the lamp on top of it. */}
      <line className="hatch-attention-antenna" x1="12" y1="6" x2="12" y2="3.5" />
      <circle className={`hatch-attention-lamp${lit ? ' lit' : ''}`} cx="12" cy="2.5" r="1.75" />

      <rect className="hatch-attention-head" x="4" y="6" width="16" height="13" rx="4" />

      <circle className={`hatch-attention-eye${lit ? ' lit' : ''}`} cx="9" cy="12" r="1.5" />
      <circle className={`hatch-attention-eye${lit ? ' lit' : ''}`} cx="15" cy="12" r="1.5" />
    </svg>
  );
}

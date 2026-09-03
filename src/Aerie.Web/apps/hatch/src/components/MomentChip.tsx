import { momentTitle, momentWords, parseMoment, urgencyOf } from '../lib/schedule';

/**
 * One of an issue's two dates, drawn small enough to sit on a card.
 *
 * A chip rather than a tint on the card itself: the board holds every issue in
 * the house, and a column of amber cards stops being read by Thursday. The
 * color is on the smallest thing that can carry it.
 */
export function MomentChip({
  kind,
  value,
  muted = false,
}: {
  kind: 'ready' | 'due';
  value: string | null;
  /**
   * Drawn without urgency. What a terminal column passes: work that shipped
   * cannot be late, and a done card glowing red is a board telling a lie.
   */
  muted?: boolean;
}) {
  const moment = parseMoment(value);
  if (!moment) return null;

  const now = new Date();
  // A ready date has no ladder of its own - it is either in the future, in
  // which case the card is folded away entirely, or it has passed and has
  // nothing left to say. Only the due date warms.
  const urgency = kind === 'due' && !muted ? urgencyOf(moment, now) : 'later';

  return (
    <span className={`hatch-chip hatch-chip-${urgency}`} title={`${kind === 'due' ? 'Due' : 'Ready'} ${momentTitle(moment)}`}>
      <span className="hatch-chip-kind">{kind === 'due' ? 'Due' : 'Ready'}</span>
      {momentWords(moment, now)}
    </span>
  );
}

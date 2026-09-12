import { Badge } from '@hatch/ui';
import type { BadgeTone } from '@hatch/ui';
import type { IssueType } from '../types';

/* One tone per type, so a column is skimmable by shape rather than read word
   by word. Epics are the loud one because there are few of them and they are
   what the board is organised around; a bug is the only type that is a
   problem, so it is the only one wearing the danger tone. */
const TONES: Record<IssueType, BadgeTone> = {
  epic: 'primary',
  story: 'muted',
  task: 'muted',
  bug: 'danger',
};

export function TypeBadge({ type }: { type: IssueType }) {
  return <Badge tone={TONES[type]}>{type}</Badge>;
}

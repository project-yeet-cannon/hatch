import { Field } from '@hatch/ui';
import { fromInputs, toInputs } from '../lib/schedule';

/**
 * Edits one of an issue's two dates: a native date picker, and beside it an
 * optional time.
 *
 * Two inputs rather than one `datetime-local` because that control cannot
 * express "a date, no time" - it wants both halves or neither, and "due on the
 * 12th" with no hour attached is the ordinary case here. Clearing the date
 * clears the field; clearing only the time leaves the date behind.
 */
export function MomentField({
  label,
  hint,
  value,
  onChange,
}: {
  label: string;
  hint?: string;
  value: string | null;
  /** The wire form, or `''` for the clear - see fromInputs. */
  onChange: (next: string) => void;
}) {
  const inputs = toInputs(value);

  return (
    <Field label={label} hint={hint}>
      <div className="hatch-moment-inputs">
        <input
          type="date"
          value={inputs.date}
          onChange={(e) => onChange(fromInputs({ ...inputs, date: e.target.value }))}
        />
        <input
          type="time"
          value={inputs.time}
          // Offering a time on a field with no date would write a value that
          // fromInputs discards, which reads as the control ignoring a keystroke.
          disabled={!inputs.date}
          aria-label={`${label} time`}
          onChange={(e) => onChange(fromInputs({ ...inputs, time: e.target.value }))}
        />
      </div>
    </Field>
  );
}

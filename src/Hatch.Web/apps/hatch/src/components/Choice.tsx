/**
 * A <select> over a fixed list of values that also draws a value that is not
 * in the list.
 *
 * Two pages set the same two things - the Playbooks matrix sets a transition's
 * model and effort, and an issue overrides them for itself - so they draw from
 * one component and from the same constants, which is what keeps what a
 * playbook may be set to and what an issue may be set to from drifting apart.
 */
export function Choice({
  label,
  value,
  options,
  placeholder,
  onChange,
}: {
  /** Left off where a <Field> already wraps the select in a <label>: a second
      name on the control would replace the visible one rather than add to it. */
  label?: string;
  value: string;
  options: readonly string[];
  /** The text on a leading empty option, for a caller where "unset" is a choice. */
  placeholder?: string;
  onChange: (value: string) => void;
}) {
  // A pinned claude-… name an operator typed by hand is offered back to them
  // rather than silently swapped for an alias the moment they touch the row.
  // The empty string is not one of those: where it means anything it means
  // "unset", and the placeholder below is already drawing it.
  const known = value !== '' && !options.includes(value) ? [value, ...options] : options;

  return (
    <select aria-label={label} value={value} onChange={(e) => onChange(e.target.value)}>
      {placeholder !== undefined && <option value="">{placeholder}</option>}
      {known.map((option) => (
        <option key={option} value={option}>
          {option}
        </option>
      ))}
    </select>
  );
}

import type { ReactNode } from 'react';
import './Field.css';

export interface FieldProps {
  /** The label's text. Always present: a control with no label is a control
      nobody can describe over the phone. */
  label: ReactNode;
  /** A standing note about the control — units, a format, what the value does.
      Renders nothing when absent rather than reserving a line. */
  hint?: ReactNode;
  /** What is wrong with the value right now. Replaces the hint while it is
      showing, because two lines under one control is two things to read. */
  error?: ReactNode;
  /** Renders the label wrapper as a <div> instead of a <label>. Needed when
      the children carry their own labelling — a checkbox with its own text, a
      radio group, a composite with an inner <label> — because a <label> inside
      a <label> is invalid HTML and its click target is undefined. */
  as?: 'label' | 'div';
  className?: string;
  children?: ReactNode;
}

/**
 * A labelled control. 105 sites in admin, the second-largest idiom in the
 * house after toned text.
 *
 * The label element *wraps* the control rather than sitting beside it with a
 * `htmlFor`. Admin's markup was the latter without the `htmlFor`, so its
 * labels were associated with nothing: clicking "Comfort low (°F)" did not
 * focus the input, and a screen reader read the input as unlabelled. Wrapping
 * gives implicit association for any control at all, including ones whose id
 * this component cannot see — and it cannot see them, because the control is
 * the app's element, not this component's.
 *
 * The hint and the error sit *outside* that <label>, which is the only reason
 * there are two nested boxes here. Text inside a <label> joins the control's
 * accessible name, so a field with a hint would announce as "Comfort low (°F)
 * degrees Fahrenheit, whole numbers" — the note read as part of the name.
 *
 * The visual result is admin's field, unchanged: one flex column, same rules.
 */
export function Field({ label, hint, error, as = 'label', className, children }: FieldProps) {
  const Control = as;
  const classes = ['aerie-field'];
  if (className) classes.push(className);

  return (
    <div className={classes.join(' ')}>
      <Control className="aerie-field__control">
        <span className="aerie-field__label">{label}</span>
        {children}
      </Control>
      {error ? (
        <span className="aerie-field__error" role="alert">
          {error}
        </span>
      ) : hint ? (
        <span className="aerie-field__hint">{hint}</span>
      ) : null}
    </div>
  );
}

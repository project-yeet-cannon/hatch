/* Picking one issue out of sixty.

   A native <select> does prefix typeahead on the option's leading text, so
   typing `745` finds nothing when every option starts with the project key -
   the only thing that matches is typing the project key and then the number
   faster than the browser's typeahead timeout. This is a button that opens a
   popup holding a search box and a list, driven by the keyboard end to end.

   Presentational and self-contained, like IssuePeek: it fetches nothing, knows
   nothing about issues beyond the cards it is handed, and every ordering and
   matching decision it makes is made in lib/issuePicker.ts, which is where
   they are tested. What is here is state and events.

   It stays in apps/hatch rather than @aerie/ui. Admin's ChannelSelect is the
   same idea and older, and merging them is a third screen's worth of argument
   - its ranking by match position, its lack of ARIA, its inline styles -
   landing on a ticket about the parent field. The bulk page is the second
   customer, and two customers is when a move to the library is worth
   proposing. */

import { useEffect, useId, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react';
import { pickerRows } from '../lib/issuePicker';
import type { IssueCard } from '../types';

/** The row that clears the value. Pinned first, and never filtered out. */
const NONE = '— none —';

export interface IssuePickerProps {
  /** The key in force, or null for none. It need not be among `candidates`: a
      retype does not re-check the parent (StageEditAsync only resolves
      ParentKey when the patch names it), so an issue can hold a parent its own
      type would now refuse, and that key still has to be drawn. */
  value: string | null;
  /** The legal candidates, in any order - the component orders them. */
  candidates: IssueCard[];
  /** The accessible name. "Parent" at the only call site today. */
  label: string;
  /** What the popup says where the candidates would be when there are none. */
  emptyMessage: string;
  /** Writes the choice: the key, or '' for the clear - the shape patchIssue
      already takes. The caller owns the request and the refusal; this awaits
      only to know when the press is over, so `onChange` must not throw. */
  onChange: (key: string) => Promise<unknown>;
  /** Whether the pinned clear row is offered. False on a picker whose press
      adds something rather than replacing what is there - there is nothing to
      clear, and removing is a button on the row that was added. */
  allowNone?: boolean;
  /** The face when `value` is null. Defaults to the clear row's own label,
      which is the right face for a field that holds one value and reads as
      empty, and the wrong one for a control that adds. */
  placeholder?: string;
}

export function IssuePicker({
  value,
  candidates,
  label,
  emptyMessage,
  onChange,
  allowNone = true,
  placeholder,
}: IssuePickerProps) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  // Nullable: when nothing matches what was typed there is nothing to take,
  // and Enter must do nothing until ↓ moves the highlight onto — none —.
  const [highlight, setHighlight] = useState<number | null>(null);
  // Its own state rather than a `busy` prop, following DescriptionEditor: this
  // is exactly the span of the promise it is already awaiting, and two
  // booleans for one await is two things that can come to disagree.
  const [saving, setSaving] = useState(false);

  const container = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const active = useRef<HTMLLIElement>(null);
  /* Set while a save this control started is in flight. The trigger is
     disabled for that span, and a browser blurs an element it disables - so
     without this, taking a row with Enter would leave focus on the document
     and the next Tab would start again from the top of the page. */
  const returning = useRef(false);

  const base = useId();
  const listId = `${base}-list`;
  const rowId = (index: number) => `${base}-row-${index}`;

  /* null is the — none — row. Where it is offered it is always first and is
     never filtered, so clearing the value is always one press away and every
     index below is arithmetic on one list - `offset` is the whole of the
     difference when it is not. */
  const offset = allowNone ? 1 : 0;
  const rows: (IssueCard | null)[] = allowNone
    ? [null, ...pickerRows(candidates, query)]
    : pickerRows(candidates, query);

  function openList() {
    const all = pickerRows(candidates, '');
    const at = all.findIndex((c) => c.key === value);
    setQuery(''); // The last query is not remembered: a remembered filter is a list that opens lying about how many candidates there are.
    // The row in force, or the top of the list - which is — none — where it is
    // offered, and nowhere at all on an empty list, since Enter must never
    // reach past the end of one.
    setHighlight(at === -1 ? (rows.length > 0 ? 0 : null) : at + offset);
    setOpen(true);
  }

  function close() {
    setOpen(false);
    trigger.current?.focus();
  }

  function typed(next: string) {
    setQuery(next);
    /* The first candidate, never — none —. A pinned first row plus "the
       highlight sits on the first row that matches" is a control where typing
       `745` and pressing Enter clears the value: — none — is pinned rather
       than matched, so ↑ is what reaches it. */
    setHighlight(pickerRows(candidates, next).length > 0 ? offset : null);
  }

  // One row at a time, stopping at the ends rather than wrapping - a wrap makes
  // "am I at the bottom" unanswerable without counting, and this list can be
  // sixty rows. From nowhere, ↓ lands on the first row and ↑ stays nowhere.
  const down = () =>
    setHighlight((h) => (rows.length === 0 ? null : h === null ? 0 : Math.min(h + 1, rows.length - 1)));
  const up = () => setHighlight((h) => (h === null ? null : Math.max(h - 1, 0)));

  async function take(row: IssueCard | null) {
    const key = row?.key ?? '';
    close();
    // Already in force: the server would no-op it, but a control that spends a
    // round trip and a page reload to do nothing feels broken on a slow link.
    if (key === (value ?? '')) return;

    returning.current = true;
    setSaving(true);
    try {
      await onChange(key);
    } finally {
      setSaving(false);
    }
  }

  // Where the focus the disable took goes back. Keyed on `saving` rather than
  // done in `take`, because the button is still disabled on the render that
  // clears it and a disabled button cannot be focused.
  useEffect(() => {
    if (saving || !returning.current) return;
    returning.current = false;
    trigger.current?.focus();
  }, [saving]);

  function onKeyDown(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'ArrowDown') {
      e.preventDefault(); // so the caret does not jump to the end of the query
      down();
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      up();
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (highlight !== null) void take(rows[highlight]);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      close();
    }
  }

  /* A pointer outside it closes it. Clicking a non-focusable area of the page
     fires no blur at all, which is why this exists as well as the focusout
     below. */
  useEffect(() => {
    if (!open) return;
    const outside = (e: PointerEvent) => {
      if (!container.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('pointerdown', outside);
    return () => document.removeEventListener('pointerdown', outside);
  }, [open]);

  /* `nearest` so the list does not jump when the row is already visible. A ref
     the highlighted row claims rather than document.getElementById: useId
     produces ids holding colons, which are legal in HTML and not legal in a
     selector without escaping. */
  useLayoutEffect(() => {
    if (open) active.current?.scrollIntoView({ block: 'nearest' });
  }, [open, highlight, query]);

  const chosen = candidates.find((c) => c.key === value) ?? null;
  const face = value === null ? (placeholder ?? NONE) : chosen ? `${chosen.key} — ${chosen.title}` : value;

  return (
    <div
      className="hatch-issue-picker"
      ref={container}
      /* React's onBlur is focusout, so it bubbles: Tab out of the search box
         moves focus to the next control on the page natively and the popup
         goes with it, with no Tab handling and no preventDefault near it. */
      onBlur={(e) => {
        if (!e.currentTarget.contains(e.relatedTarget)) setOpen(false);
      }}
    >
      <button
        type="button"
        ref={trigger}
        className="hatch-issue-picker-button"
        role="combobox"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={listId}
        // The name is the label and nothing else: the face and the hint stay out of it.
        aria-label={label}
        disabled={saving}
        onClick={() => (open ? close() : openList())}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown') {
            e.preventDefault();
            openList();
          }
        }}
      >
        {face}
      </button>

      {open && (
        <div className="hatch-issue-picker-popup">
          {/* type="text", not type="search": Escape inside a search input clears
              it in WebKit and Blink before the keydown reaches us, and base.css
              enumerates the input types it styles on purpose. */}
          <input
            type="text"
            className="hatch-issue-picker-search"
            // The popup is mounted fresh on every open, so this focuses each time.
            autoFocus
            value={query}
            aria-label={`Filter ${label.toLowerCase()} candidates`}
            aria-controls={listId}
            aria-autocomplete="list"
            // On a textbox, pointing at an option is what announces the
            // highlight without moving focus off the query.
            aria-activedescendant={highlight === null ? undefined : rowId(highlight)}
            onChange={(e) => typed(e.target.value)}
            onKeyDown={onKeyDown}
          />

          {candidates.length === 0 && <p className="hatch-issue-picker-note">{emptyMessage}</p>}
          {candidates.length > 0 && rows.length === offset && (
            <p className="hatch-issue-picker-note">Nothing matches “{query.trim()}”.</p>
          )}

          <ul className="hatch-issue-picker-list" id={listId} role="listbox" aria-label={label}>
            {rows.map((row, index) => {
              const key = row?.key ?? '';
              const current = key === (value ?? '');
              const classes = ['hatch-issue-picker-row'];
              if (index === highlight) classes.push('is-highlighted');
              if (current) classes.push('is-current');

              return (
                <li
                  key={key || NONE}
                  id={rowId(index)}
                  role="option"
                  aria-selected={current}
                  ref={index === highlight ? active : null}
                  className={classes.join(' ')}
                  /* The press must not blur the search box: a blur closes the
                     popup and the click then lands on a node that is no longer
                     there. ChannelSelect does the same, for the same reason. */
                  onMouseDown={(e) => e.preventDefault()}
                  onClick={() => void take(row)}
                  onMouseEnter={() => setHighlight(index)}
                >
                  {row ? (
                    <>
                      <span className="hatch-issue-picker-key">{row.key}</span>
                      {row.title}
                      {/* aria-hidden: aria-selected above already says this row
                          is the one in force, and a tick read out as well says
                          it twice. This is the same fact, for an eye. */}
                      {current && (
                        <span className="hatch-issue-picker-mark" aria-hidden="true">
                          ✓
                        </span>
                      )}
                    </>
                  ) : (
                    NONE
                  )}
                </li>
              );
            })}
          </ul>
        </div>
      )}
    </div>
  );
}

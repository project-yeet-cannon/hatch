import { useState } from 'react';
import type { FormEvent } from 'react';
import { InlineError } from '../../components/Notices';
import { useMutation } from '../../lib/useResource';
import { createList, deleteList, updateList } from './api';
import { colorOf, DEFAULT_ICON, LIST_COLORS, LIST_ICONS } from './palette';
import type { Item, ListSummary } from './types';

/*
  Gather's own pieces. The generic ones - loading, errors, empty - come from the
  shell's Notices, so nothing here restates what a spinner looks like.
*/

/** A list's icon in its colour, at the size a card wants. */
export function ListIcon({ icon, color }: { icon: string | null; color: string | null }) {
  return (
    <span className={`gather-icon gather-tint-${colorOf(color)}`} aria-hidden="true">
      {icon ?? DEFAULT_ICON}
    </span>
  );
}

/**
 * One row of an item.
 *
 * The whole row toggles - deliberately unlike Storage, whose rows open an
 * editor. These are tapped one-handed while walking an aisle, so the check
 * target is the row and everything else is a smaller, deliberate aim: the ⋯ on
 * the right is the only way to the editor.
 *
 * A real checkbox inside a label, so the semantics and the keyboard behaviour
 * come free rather than being reconstructed on a div.
 */
export function ItemRow({
  item,
  pending,
  onToggle,
  onEdit,
}: {
  item: Item;
  pending: boolean;
  onToggle: (isChecked: boolean) => void;
  onEdit: () => void;
}) {
  return (
    <li className={`gather-item${item.isChecked ? ' checked' : ''}${pending ? ' pending' : ''}`}>
      <label className="gather-item-main">
        <input
          type="checkbox"
          checked={item.isChecked}
          onChange={(e) => onToggle(e.target.checked)}
          className="gather-check"
        />
        <span className="gather-item-text">
          <span className="gather-item-name">
            {item.name}
            {item.quantity && <span className="gather-qty">{item.quantity}</span>}
          </span>
          {item.note && <span className="gather-item-note">{item.note}</span>}
        </span>
      </label>
      <button type="button" className="gather-item-more" onClick={onEdit} aria-label={`Edit ${item.name}`}>
        ⋯
      </button>
    </li>
  );
}

function IconPicker({ value, onChange }: { value: string; onChange: (icon: string) => void }) {
  return (
    <div className="gather-picker" role="group" aria-label="Icon">
      {LIST_ICONS.map((icon) => (
        <button
          type="button"
          key={icon}
          className={`gather-swatch${icon === value ? ' selected' : ''}`}
          onClick={() => onChange(icon)}
          aria-pressed={icon === value}
          aria-label={icon}
        >
          {icon}
        </button>
      ))}
    </div>
  );
}

function ColorPicker({ value, onChange }: { value: string; onChange: (color: string) => void }) {
  return (
    <div className="gather-picker" role="group" aria-label="Colour">
      {LIST_COLORS.map((color) => (
        <button
          type="button"
          key={color}
          className={`gather-swatch gather-swatch-color gather-tint-${color}${color === value ? ' selected' : ''}`}
          onClick={() => onChange(color)}
          aria-pressed={color === value}
          aria-label={color}
        />
      ))}
    </div>
  );
}

/**
 * Name, icon and colour - the whole of what a list is. One form for both the
 * jobs that need it: creating a list from the index, and the settings panel on
 * the list itself. `list` being absent is what makes it the former.
 */
export function ListForm({
  list,
  onSaved,
  onCancel,
  onDeleted,
}: {
  list?: ListSummary;
  onSaved: (list: ListSummary) => void;
  onCancel: () => void;
  onDeleted?: () => void;
}) {
  const [name, setName] = useState(list?.name ?? '');
  const [icon, setIcon] = useState(list?.icon ?? DEFAULT_ICON);
  const [color, setColor] = useState<string>(colorOf(list?.color));
  const save = useMutation();
  const remove = useMutation();

  async function submit(event: FormEvent) {
    event.preventDefault();
    const trimmed = name.trim();
    if (!trimmed) return;

    let saved: ListSummary | undefined;
    const ok = await save.run(async () => {
      const request = { name: trimmed, icon, color };
      saved = list ? await updateList(list.id, request) : await createList(request);
    });
    if (ok && saved) onSaved(saved);
  }

  /**
   * The one irreversible action in Gather, so it says out loud what goes with
   * it. Clearing checked items doesn't ask - that button already names its own
   * count and re-adding is one tap - but a list and everything on it is not
   * something to lose to a mis-tap.
   */
  async function destroy() {
    if (!list || !onDeleted) return;

    const count = list.openCount + list.checkedCount;
    const fate = count > 0 ? ` The ${count} ${count === 1 ? 'item' : 'items'} on it go too.` : '';
    if (!confirm(`Delete "${list.name}"?${fate}`)) return;

    if (await remove.run(() => deleteList(list.id))) onDeleted();
  }

  return (
    <form className="card gather-form" onSubmit={submit}>
      <label className="gather-field">
        <span>Name</span>
        <input
          type="text"
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="Grocery"
          autoCapitalize="words"
          autoFocus={!list}
        />
      </label>

      <div className="gather-field">
        <span>Icon</span>
        <IconPicker value={icon} onChange={setIcon} />
      </div>

      <div className="gather-field">
        <span>Colour</span>
        <ColorPicker value={color} onChange={setColor} />
      </div>

      {save.error && <InlineError message={save.error} />}
      {remove.error && <InlineError message={remove.error} />}

      <div className="gather-form-actions">
        <button type="submit" className="btn-primary" disabled={save.busy || !name.trim()}>
          {list ? 'Save' : 'Create'}
        </button>
        <button type="button" onClick={onCancel}>
          Cancel
        </button>
        {list && onDeleted && (
          <button type="button" className="btn-danger gather-form-delete" onClick={destroy} disabled={remove.busy}>
            Delete
          </button>
        )}
      </div>
    </form>
  );
}

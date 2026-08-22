import { useCallback, useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import { faArrowLeft, faCheck, faPlus, faXmark } from '@fortawesome/free-solid-svg-icons';
import { getGatherSource } from '../dataSource';
import { clientLogger } from '../lib/clientLogger';
import { DEFAULT_LIST_ICON, tintBackgroundOf } from '../lib/listColor';
import { ACTIVITY_EVENTS } from '../lib/kioskLifecycle';
import { useViewportInset } from '../hooks/useViewportInset';
import type { GatherItem, GatherListDetail, GatherListSummary } from '../types';

/**
 * Gather, full-screen over the dashboard. The layout rule that shapes all of
 * this: the tablets are portrait, the keyboard owns the bottom fifth, and
 * whether the viewport resizes for it is untested - so the add field and every
 * button live in the top of the column and only the item list runs down into
 * the part the keyboard may cover. A bottom-pinned add field is the right
 * design on a phone and precisely the wrong one here.
 */

/** Fast enough that the kitchen and the aisle converge; slow enough to be polling. */
const POLL_INTERVAL_MS = 10_000;

/**
 * After a tap, a poll updates values but not the row ORDER. The server sinks a
 * checked item to the bottom, and a list re-sorting under a finger mid-shop is
 * how you check off the wrong thing - so the re-sort waits for a pause.
 */
const SETTLE_MS = 4_000;

/**
 * The overlay's own idle timeout, longer than the dashboard's 30s because
 * standing at the wall reading a list is a legitimate way to spend a minute.
 * Closing hands control back to the normal lifecycle, so the wall always finds
 * its way home on its own - a tablet left on the grocery list is a regression in
 * what the wall is for.
 */
const OVERLAY_IDLE_MS = 90_000;

/** A destructive sweep on a wall anyone walks past gets a second tap, not a dialog. */
const CONFIRM_WINDOW_MS = 4_000;

/**
 * How long an optimistic tick defends itself against what the server says.
 * It has to expire: another device un-checking the same item is a disagreement
 * the server has to win, and without a deadline the wall would hold a tick that
 * is no longer on the list until someone closed the overlay. The family shell
 * settled on the same 30s for the same reason (docs/plans/gather.md, Phase 2).
 */
const OPTIMISTIC_TTL_MS = 30_000;

/** Soft keyboards don't reliably produce keydown; typing still counts as presence. */
const OVERLAY_ACTIVITY_EVENTS = [...ACTIVITY_EVENTS, 'input'] as const;

export function GatherOverlay({
  listId,
  lists,
  onClose,
}: {
  /** The list to open, or null to land on the picker. */
  listId: string | null;
  lists: GatherListSummary[];
  onClose: () => void;
}) {
  const [openId, setOpenId] = useState<string | null>(listId);
  const inset = useViewportInset();

  // Held in a ref so the timer below survives the parent re-rendering, which it
  // does every 15s for the clock alone. An effect that re-ran on a new onClose
  // identity would re-arm the idle close forever and the wall would sit on the
  // grocery list until someone walked past.
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  // Idle close. Deliberately not shared with the dashboard's timer: that one is
  // held while this is open (see hooks/useKioskLifecycle.ts), and this replaces
  // it with a longer one for as long as someone is standing here.
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | null = null;

    const arm = () => {
      if (timer !== null) clearTimeout(timer);
      timer = setTimeout(() => {
        clientLogger.info('Gather overlay idle; returning to the dashboard', { idleMs: OVERLAY_IDLE_MS });
        onCloseRef.current();
      }, OVERLAY_IDLE_MS);
    };

    arm();
    for (const event of OVERLAY_ACTIVITY_EVENTS) {
      window.addEventListener(event, arm, { passive: true, capture: true });
    }
    return () => {
      if (timer !== null) clearTimeout(timer);
      for (const event of OVERLAY_ACTIVITY_EVENTS) {
        window.removeEventListener(event, arm, { capture: true });
      }
    };
  }, []);

  return (
    <div className="hf-gather-overlay" style={{ paddingBottom: inset }} role="dialog" aria-modal="true" aria-label="Gather">
      {openId === null ? (
        <ListPicker lists={lists} onPick={setOpenId} onClose={onClose} />
      ) : (
        <ListScreen
          // A fresh mount per list: without it React reuses the instance and
          // the previous list's items, optimistic ticks and half-typed draft
          // all survive the switch until the first fetch lands.
          key={openId}
          listId={openId}
          fallbackName={lists.find((l) => l.id === openId)?.name ?? 'List'}
          onBack={lists.length > 1 ? () => setOpenId(null) : null}
          onClose={onClose}
        />
      )}
    </div>
  );
}

/** Only reachable from inside the overlay - the dashboard tile is the usual way in. */
function ListPicker({
  lists,
  onPick,
  onClose,
}: {
  lists: GatherListSummary[];
  onPick: (id: string) => void;
  onClose: () => void;
}) {
  return (
    <>
      <div className="hf-gather-bar">
        <span className="hf-gather-title">Lists</span>
        <button type="button" className="hf-gather-icon-btn" onClick={onClose} aria-label="Close">
          <FontAwesomeIcon icon={faXmark} />
        </button>
      </div>
      <div className="hf-gather-scroll">
        {lists.map((list) => (
          <button key={list.id} type="button" className="hf-gather-pick" onClick={() => onPick(list.id)}>
            <span className="hf-gather-pick-icon" style={{ background: tintBackgroundOf(list.color) }} aria-hidden="true">
              {list.icon ?? DEFAULT_LIST_ICON}
            </span>
            <span className="hf-gather-pick-name">{list.name}</span>
            <span className="hf-gather-pick-count">{list.openCount === 0 ? '—' : list.openCount}</span>
          </button>
        ))}
      </div>
    </>
  );
}

function ListScreen({
  listId,
  fallbackName,
  onBack,
  onClose,
}: {
  listId: string;
  fallbackName: string;
  onBack: (() => void) | null;
  onClose: () => void;
}) {
  const [source] = useState(getGatherSource);
  const [detail, setDetail] = useState<GatherListDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [added, setAdded] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  const [confirmingClear, setConfirmingClear] = useState(false);
  /** Optimistic check state, cleared per item once a snapshot agrees or it ages out. */
  const [pending, setPending] = useState<Record<string, { checked: boolean; at: number }>>({});

  const inputRef = useRef<HTMLInputElement>(null);
  const lastTapRef = useRef(0);
  /** Adds are serialized, so a fast run of items can't land out of order or race the upsert. */
  const queueRef = useRef<Promise<unknown>>(Promise.resolve());

  const apply = useCallback((next: GatherListDetail) => {
    setDetail((prev) => {
      // Values always update; the order only re-sorts once nobody is mid-tap.
      if (prev === null || Date.now() - lastTapRef.current >= SETTLE_MS) return next;

      const byId = new Map(next.items.map((item) => [item.id, item]));
      const kept: GatherItem[] = [];
      for (const item of prev.items) {
        const fresh = byId.get(item.id);
        if (fresh) {
          kept.push(fresh);
          byId.delete(item.id);
        }
      }
      // Anything new arrived from another device; it goes where the server put it.
      return { list: next.list, items: [...kept, ...byId.values()] };
    });

    setPending((prev) => {
      const now = Date.now();
      let changed = false;
      const remaining = { ...prev };
      // An override that the snapshot agrees with has done its job; one the
      // snapshot still contradicts after the TTL has lost the argument.
      for (const [id, override] of Object.entries(remaining)) {
        const fresh = next.items.find((item) => item.id === id);
        const settled = fresh !== undefined && fresh.isChecked === override.checked;
        if (settled || now - override.at >= OPTIMISTIC_TTL_MS || fresh === undefined) {
          delete remaining[id];
          changed = true;
        }
      }
      return changed ? remaining : prev;
    });
  }, []);

  const load = useCallback(async () => {
    try {
      apply(await source.getList(listId));
      setError(null);
    } catch (err: unknown) {
      const reason = err instanceof Error ? err.message : String(err);
      clientLogger.error('Gather list load failed', { listId, reason });
      setError(reason);
    }
  }, [apply, listId, source]);

  useEffect(() => {
    void load();
    const poll = setInterval(() => void load(), POLL_INTERVAL_MS);
    return () => clearInterval(poll);
  }, [load]);

  useEffect(() => {
    if (added === null) return;
    const clear = setTimeout(() => setAdded(null), 2_500);
    return () => clearTimeout(clear);
  }, [added]);

  useEffect(() => {
    if (!confirmingClear) return;
    const cancel = setTimeout(() => setConfirmingClear(false), CONFIRM_WINDOW_MS);
    return () => clearTimeout(cancel);
  }, [confirmingClear]);

  function toggle(item: GatherItem) {
    const now = Date.now();
    const next = !(pending[item.id]?.checked ?? item.isChecked);
    lastTapRef.current = now;
    setPending((prev) => ({ ...prev, [item.id]: { checked: next, at: now } }));
    setError(null);

    source.setChecked(listId, item.id, next).catch((err: unknown) => {
      const reason = err instanceof Error ? err.message : String(err);
      clientLogger.error('Gather check failed', { listId, itemId: item.id, reason });
      setError(reason);
      // Put the row back rather than leaving a tick that isn't on the list.
      setPending((prev) => {
        const reverted = { ...prev };
        delete reverted[item.id];
        return reverted;
      });
    });
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    const name = draft.trim();
    if (name.length === 0) return;

    // Cleared, never disabled: disabling the input drops focus, and on a tablet
    // that takes the keyboard down with it - which would cost a re-tap per item,
    // the exact thing this field exists to avoid.
    setDraft('');
    setError(null);
    // Explicit rather than assumed: the field is what a run of items is typed
    // into, and a re-tap per item is the cost of getting this wrong. A focus()
    // on an already-focused input is a no-op, so this can only help.
    inputRef.current?.focus();

    queueRef.current = queueRef.current
      .then(() => source.addItem(listId, name))
      .then(async () => {
        setAdded(name);
        clientLogger.info('Gather item added', { listId, name });
        // A poll rather than a splice: the server decides where a new item sits
        // and whether it merged onto one already there, and it is 10ms away.
        lastTapRef.current = 0;
        await load();
      })
      .catch((err: unknown) => {
        const reason = err instanceof Error ? err.message : String(err);
        clientLogger.error('Gather add failed', { listId, name, reason });
        setError(`Couldn’t add “${name}” — ${reason}`);
        // Hand the text back, unless they've started typing the next thing.
        setDraft((current) => (current.length === 0 ? name : current));
      });
  }

  function clearChecked() {
    if (!confirmingClear) {
      setConfirmingClear(true);
      return;
    }
    setConfirmingClear(false);

    source
      .clearChecked(listId)
      .then((deleted) => {
        clientLogger.info('Gather checked items cleared', { listId, deleted });
        lastTapRef.current = 0;
        return load();
      })
      .catch((err: unknown) => {
        const reason = err instanceof Error ? err.message : String(err);
        clientLogger.error('Gather clear-checked failed', { listId, reason });
        setError(reason);
      });
  }

  const items = detail?.items ?? [];
  const isChecked = (item: GatherItem) => pending[item.id]?.checked ?? item.isChecked;
  const openCount = items.filter((item) => !isChecked(item)).length;
  const checkedCount = items.length - openCount;

  return (
    <>
      <div className="hf-gather-bar">
        {onBack ? (
          <button type="button" className="hf-gather-icon-btn" onClick={onBack} aria-label="All lists">
            <FontAwesomeIcon icon={faArrowLeft} />
          </button>
        ) : (
          <span className="hf-gather-icon-btn placeholder" aria-hidden="true" />
        )}
        <span className="hf-gather-title">{detail?.list.name ?? fallbackName}</span>
        <button type="button" className="hf-gather-icon-btn" onClick={onClose} aria-label="Close">
          <FontAwesomeIcon icon={faXmark} />
        </button>
      </div>

      <form className="hf-gather-add" onSubmit={submit}>
        <input
          ref={inputRef}
          className="hf-gather-input"
          value={draft}
          onChange={(event) => setDraft(event.target.value)}
          placeholder="Add an item"
          aria-label="Add an item"
          type="text"
          // Autocorrect mangling a brand name is worse than a lowercase one, and
          // a shopping list is exactly where it would (see docs/plans/gather.md).
          autoCorrect="off"
          autoCapitalize="words"
          autoComplete="off"
          spellCheck={false}
          // Not "done", which tells an IME to take the keyboard down after the
          // action - the whole point of this field is a run of items in a row.
          // "send" is the repeated-entry hint, the same one a message box uses.
          enterKeyHint="send"
          maxLength={120}
        />
        <button type="submit" className="hf-gather-add-btn" aria-label="Add">
          <FontAwesomeIcon icon={faPlus} />
        </button>
      </form>

      <div className="hf-gather-meta">
        <span className="hf-gather-counts">
          {error !== null ? (
            <span className="hf-gather-error">{error}</span>
          ) : added !== null ? (
            <span className="hf-gather-added">
              <FontAwesomeIcon icon={faCheck} /> Added {added}
            </span>
          ) : detail === null ? (
            'Loading…'
          ) : openCount === 0 ? (
            'Nothing to get'
          ) : (
            `${openCount} to get`
          )}
        </span>
        {checkedCount > 0 && (
          <button
            type="button"
            className={`hf-gather-clear${confirmingClear ? ' confirming' : ''}`}
            onClick={clearChecked}
          >
            {confirmingClear ? `Remove ${checkedCount}?` : `Clear ${checkedCount}`}
          </button>
        )}
      </div>

      <div className="hf-gather-scroll">
        {items.map((item) => {
          const checked = isChecked(item);
          return (
            <button
              key={item.id}
              type="button"
              className={`hf-gather-row${checked ? ' checked' : ''}`}
              onClick={() => toggle(item)}
            >
              <span className="hf-gather-box">{checked && <FontAwesomeIcon icon={faCheck} />}</span>
              <span className="hf-gather-item">
                <span className="hf-gather-item-name">{item.name}</span>
                {item.note !== null && <span className="hf-gather-item-note">{item.note}</span>}
              </span>
              {item.quantity !== null && <span className="hf-gather-qty">{item.quantity}</span>}
            </button>
          );
        })}
        {detail !== null && items.length === 0 && (
          <p className="hf-gather-empty">Nothing on this list yet — add the first thing above.</p>
        )}
      </div>
    </>
  );
}

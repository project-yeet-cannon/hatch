import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Button, Modal } from '@aerie/ui';
import { getIssue, patchIssue } from '../api/client';
import { DescriptionEditor } from './DescriptionEditor';
import { MomentChip } from './MomentChip';
import { StatusPill } from './StatusPill';
import { TypeBadge } from './TypeBadge';
import { appHref } from '../lib/basename';
import { message } from '../lib/errors';
import type { IssueCard, Status } from '../types';

/** What the peek had to ask for, and the card it asked about. `description`
    undefined is "not here yet", which is what the Loading line reads off. */
interface Asked {
  key: string | null;
  description?: string;
  loadError?: string;
  saveError?: string;
}

/**
 * What a card says when you click it: everything the board already knew, at
 * once, plus the one field people open a card to read.
 *
 * Key, type, title, column, parent and dates came down with the board
 * (IssueCardDto), so the dialog paints the moment it opens. The description is
 * the exception, and it is fetched here rather than shipped with every card:
 * putting it on the board would slow the request every visit makes to save one
 * on the clicks that actually want a brief. So it arrives a moment later, under
 * its own heading, and can be fixed where it is read - skimming a column and
 * correcting a brief are then the same gesture.
 *
 * The comments and the history are still deliberately not fetched: they are the
 * reason the issue page exists, and so are the title, the type, the column, the
 * parent and the dates. This dialog adds one field, not a second issue page,
 * and it still hands over two ways to go further: the full issue in this tab,
 * or in a new one.
 */
export function IssuePeek({
  card,
  status,
  onClose,
}: {
  card: IssueCard | null;
  /** The column it is sitting in, or undefined if the board has moved underneath. */
  status?: Status;
  onClose: () => void;
}) {
  const key = card?.key ?? null;
  /* Everything that had to be asked for, and the card it was asked for.

     One slot rather than three, because which card the answers belong to is
     the thing that has to be right: a response is applied only while its own
     key is still the key on screen, and that guard is the state itself rather
     than a flag beside it, so there is nothing to fall out of step with what
     is drawn. It covers the fetch and the save alike - a save has no cleanup
     function to hang a cancellation flag on, and two stale rules that differ
     is the drift this component is otherwise removing. Without it, a request
     resolving after the operator has clicked a different card paints the old
     issue's brief on the new one. */
  const [asked, setAsked] = useState<Asked>({ key });

  /* Reset while rendering rather than in an effect, which is the same shape as
     the draft reconciliation in DescriptionEditor: the new card's title and
     the old card's description never reach the screen together, because React
     re-renders on this before it commits. An effect would clear it a frame
     late. BoardPage mounts the peek permanently with card={null} while it is
     closed, so nothing here ever unmounts on its own and this is the only
     thing that clears it. */
  if (asked.key !== key) setAsked({ key });

  /** Records an answer, if the card it was asked about is still the one on screen. */
  const apply = useCallback((about: string, answer: Partial<Asked>) => {
    setAsked((prev) => (prev.key === about ? { ...prev, ...answer } : prev));
  }, []);

  useEffect(() => {
    if (!key) return;
    getIssue(key)
      .then((issue) => apply(key, { description: issue.description }))
      .catch((err: unknown) => apply(key, { loadError: message(err) }));
  }, [key, apply]);

  /* Never throws: DescriptionEditor awaits this and reads the answer, and a
     rejection would leave it reading busy for ever. The board is not reloaded -
     no card draws a description, so there is nothing out there for this to
     change. */
  const save = useCallback(
    async (next: string): Promise<boolean> => {
      if (!key) return false;
      apply(key, { saveError: undefined });
      try {
        const issue = await patchIssue(key, { description: next });
        apply(key, { description: issue.description });
        return true;
      } catch (err) {
        apply(key, { saveError: message(err) });
        return false;
      }
    },
    [key, apply],
  );

  // Rendered unconditionally so the dialog's own open/closed handling - focus,
  // escape, the scrim - is the one that runs. Its title needs a card, though,
  // so a closed peek has nothing to say. Below every hook: the rules of hooks
  // are an error here.
  if (!card) return <Modal open={false} onClose={onClose} title="" />;

  const terminal = status?.isTerminal ?? false;
  const head = <h4 className="hatch-section-title">Description</h4>;

  return (
    <Modal open onClose={onClose} title={card.key}>
      <div className="hatch-peek">
        <div className="hatch-peek-meta">
          <TypeBadge type={card.type} />
          {status && <StatusPill status={status} />}
          {card.parentKey && (
            <Link to={`/issues/${card.parentKey}`} onClick={onClose}>
              ↳ {card.parentKey}
            </Link>
          )}
        </div>

        <p className="hatch-peek-title">{card.title}</p>

        {(card.readyAt || card.dueAt) && (
          <div className="hatch-card-dates">
            <MomentChip kind="ready" value={card.readyAt} />
            <MomentChip kind="due" value={card.dueAt} muted={terminal} />
          </div>
        )}

        <div className="hatch-peek-description">
          {asked.loadError ? (
            <>
              <div className="hatch-section-head">{head}</div>
              <p className="text-danger">{asked.loadError}</p>
            </>
          ) : asked.description === undefined ? (
            <>
              <div className="hatch-section-head">{head}</div>
              <p className="text-muted">Loading…</p>
            </>
          ) : (
            /* The key is load-bearing: it is what gives a second card a fresh
               draft and a fresh preview flag rather than the first card's. */
            <DescriptionEditor
              key={card.key}
              title={head}
              value={asked.description}
              onSave={save}
              error={asked.saveError ?? null}
              editorClassName="hatch-peek-grows"
              rows={8}
            />
          )}
        </div>

        <div className="hatch-form-actions">
          <Button onClick={onClose}>Close</Button>
          {/* An anchor rather than a <Link>: target="_blank" opens a second
              document, which React Router does not route. appHref is what keeps
              that second document on the right prefix - see lib/basename.ts. */}
          <Button as="a" href={appHref(`/issues/${card.key}`)} target="_blank" rel="noreferrer">
            New tab ↗
          </Button>
          <Button as={Link} variant="primary" to={`/issues/${card.key}`} onClick={onClose}>
            Open the issue
          </Button>
        </div>
      </div>
    </Modal>
  );
}

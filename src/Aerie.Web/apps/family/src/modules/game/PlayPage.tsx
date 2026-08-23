import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ErrorNote, Loading } from '../../components/Notices';
import { errorMessage, useResource } from '../../lib/useResource';
import { getCapability, getCurrentWorld, getWorld, repair, reportBreakage, saveRecords, takeTurn, undo } from './api';
import { GameFrame } from './GameFrame';
import { HistoryPanel } from './HistoryPanel';
import { worldsPath } from './routes';
import type { Breakage, Capability, GameRecord, ModelChoice, World } from './types';

/**
 * How many times a crash is handed back to the model before the game is simply
 * put back to what worked.
 *
 * Two, because the value of a third try is small and its cost is not: every
 * attempt is another half-minute of a stopped game in front of someone who
 * cannot read the reason. Going back is instant and always works.
 */
const MAX_REPAIR_ATTEMPTS = 2;

/** Long enough to read a sentence aloud, short enough not to sit over the game. */
const NOTE_SECONDS = 9;

/** A beaten record is worth one write, not one per frame while a ball is still rolling. */
const RECORD_SAVE_DELAY_MS = 1200;

type Working =
  | { kind: 'idle' }
  | { kind: 'writing'; prompt: string }
  | { kind: 'fixing' };

/**
 * The whole game: a screen you play on and a box you type into.
 *
 * The two halves are deliberately independent. Typing a request does not stop
 * the game - the frame keeps running the old version while the new one is being
 * written, because a thirty-second wait staring at a frozen screen is the
 * difference between this being fun and being a chore. The swap happens when
 * the code arrives.
 */
export function PlayPage() {
  const { worldId } = useParams();
  const world = useResource<World>(
    `game:world:${worldId ?? 'current'}`,
    (signal) => (worldId ? getWorld(worldId, signal) : getCurrentWorld(signal)),
  );
  const capability = useResource<Capability>('game:capability', getCapability);

  const [working, setWorking] = useState<Working>({ kind: 'idle' });
  const [prompt, setPrompt] = useState('');
  const [model, setModel] = useState<ModelChoice>('Quick');
  const [note, setNote] = useState<{ text: string; extra: string | null } | null>(null);
  const [trouble, setTrouble] = useState<string | null>(null);
  const [showHistory, setShowHistory] = useState(false);

  // The message handler and the repair loop both need today's world and today's
  // busy state, and neither is re-registered per render.
  const current = useRef<World | null>(null);
  current.current = world.data;
  const busy = useRef(false);
  const repairs = useRef(0);

  const apply = useCallback(
    (next: World) => {
      world.set(next);
      if (next.current?.summary) setNote({ text: next.current.summary, extra: next.current.extra });
    },
    [world],
  );

  // ---- records

  const pending = useRef<GameRecord[] | null>(null);
  const saveTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const onRecords = useCallback((records: GameRecord[]) => {
    pending.current = records;
    if (saveTimer.current) clearTimeout(saveTimer.current);
    saveTimer.current = setTimeout(() => {
      const id = current.current?.world.id;
      const toSave = pending.current;
      if (!id || !toSave) return;
      // A lost personal best is a small sadness; an error box over a game
      // because a score failed to save is a bigger one.
      void saveRecords(id, toSave).catch(() => undefined);
    }, RECORD_SAVE_DELAY_MS);
  }, []);

  useEffect(() => () => {
    if (saveTimer.current) clearTimeout(saveTimer.current);
  }, []);

  // ---- turns

  const send = useCallback(async () => {
    const id = current.current?.world.id;
    const text = prompt.trim();
    if (!id || text.length === 0 || busy.current) return;

    busy.current = true;
    setWorking({ kind: 'writing', prompt: text });
    setTrouble(null);
    setPrompt('');

    try {
      const next = await takeTurn(id, text, model);
      // A turn that lands is a fresh start for the repair budget: whatever
      // crashed before is not what is running now.
      repairs.current = 0;
      apply(next);
    } catch (err) {
      setTrouble(errorMessage(err));
      // Give the words back, so a failed turn is a retry rather than
      // remembering what you typed.
      setPrompt(text);
    } finally {
      busy.current = false;
      setWorking({ kind: 'idle' });
    }
  }, [apply, model, prompt]);

  /**
   * The game crashed. Try the model on it, twice at most, then put the last
   * working version back and say so plainly.
   */
  const onError = useCallback(
    async (breakage: Breakage) => {
      const game = current.current;
      const live = game?.current;
      // A frame still finishing the previous version can report a crash that
      // belongs to code nobody is running any more.
      if (!game || !live || breakage.versionId !== live.id || busy.current) return;

      busy.current = true;
      try {
        if (repairs.current < MAX_REPAIR_ATTEMPTS) {
          repairs.current += 1;
          setWorking({ kind: 'fixing' });
          apply(await repair(game.world.id, breakage));
          return;
        }

        await reportBreakage(game.world.id, breakage).catch(() => undefined);
        apply(await undo(game.world.id));
        setNote({ text: "That one didn't work, so I put the game back.", extra: null });
      } catch (err) {
        setTrouble(errorMessage(err));
      } finally {
        busy.current = false;
        setWorking({ kind: 'idle' });
      }
    },
    [apply],
  );

  const goBack = useCallback(async () => {
    const id = current.current?.world.id;
    if (!id || busy.current) return;
    busy.current = true;
    setTrouble(null);
    try {
      apply(await undo(id));
      repairs.current = 0;
    } catch (err) {
      setTrouble(errorMessage(err));
    } finally {
      busy.current = false;
    }
  }, [apply]);

  useEffect(() => {
    if (!note) return;
    const timer = setTimeout(() => setNote(null), NOTE_SECONDS * 1000);
    return () => clearTimeout(timer);
  }, [note]);

  if (world.loading && !world.data) return <Loading />;
  if (world.error) return <ErrorNote message={world.error} onRetry={world.reload} />;
  if (!world.data) return <ErrorNote message="No game here." onRetry={world.reload} />;

  const game = world.data;
  const canAuthor = capability.data?.canAuthor !== false;

  return (
    <div className="game">
      <div className="game-stage">
        <GameFrame
          code={game.code}
          versionId={game.current?.id ?? null}
          records={game.records}
          onError={onError}
          onRecords={onRecords}
        />

        {note && (
          <div className="game-note" role="status">
            <span className="game-note-text">{note.text}</span>
            {note.extra && <span className="game-note-extra">✨ {note.extra}</span>}
          </div>
        )}

        <div className="game-tools">
          <button type="button" className="game-tool" onClick={goBack} title="Go back one step">
            ↩
          </button>
          <button
            type="button"
            className="game-tool"
            onClick={() => setShowHistory((open) => !open)}
            title="Everything we made"
          >
            🕘
          </button>
          <Link to={worldsPath} className="game-tool" title="All our games">
            🎮
          </Link>
        </div>
      </div>

      {working.kind !== 'idle' && (
        <p className="game-working" role="status">
          <span className="game-spinner" aria-hidden="true" />
          {working.kind === 'writing' ? `Making “${working.prompt}”…` : 'Fixing it…'}
        </p>
      )}

      {trouble && <p className="game-trouble">{trouble}</p>}

      {canAuthor ? (
        <form
          className="game-ask"
          onSubmit={(event) => {
            event.preventDefault();
            void send();
          }}
        >
          <input
            className="game-input"
            value={prompt}
            onChange={(event) => setPrompt(event.target.value)}
            placeholder="What should happen?"
            aria-label="What should happen?"
            autoComplete="off"
            enterKeyHint="go"
            maxLength={500}
            disabled={working.kind !== 'idle'}
          />
          <button type="submit" className="game-go" disabled={working.kind !== 'idle' || prompt.trim().length === 0}>
            Go
          </button>
          <div className="game-speed" role="group" aria-label="How hard to think">
            <button
              type="button"
              className={`game-speed-choice${model === 'Quick' ? ' active' : ''}`}
              onClick={() => setModel('Quick')}
            >
              ⚡ Quick
            </button>
            <button
              type="button"
              className={`game-speed-choice${model === 'Careful' ? ' active' : ''}`}
              onClick={() => setModel('Careful')}
            >
              🧠 Careful
            </button>
          </div>
        </form>
      ) : (
        <p className="game-blocked">{capability.data?.reason}</p>
      )}

      {showHistory && (
        <HistoryPanel
          worldId={game.world.id}
          currentVersionId={game.current?.id ?? null}
          versionCount={game.world.versionCount}
          onClose={() => setShowHistory(false)}
          onRestored={(next) => {
            repairs.current = 0;
            apply(next);
            setShowHistory(false);
          }}
        />
      )}
    </div>
  );
}

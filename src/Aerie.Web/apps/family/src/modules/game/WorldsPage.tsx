import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { ErrorNote, InlineError, Loading } from '../../components/Notices';
import { useMutation, useResource } from '../../lib/useResource';
import { createWorld, deleteWorld, getWorlds } from './api';
import { worldPath } from './routes';
import type { WorldSummary } from './types';

/** Enough to pick from without a picker; a child chooses one by how it looks. */
const ICONS = ['🎮', '🐶', '🚀', '🌈', '🍩', '🐙', '🏰', '⚽'];

/**
 * Every game the house has made.
 *
 * Several worlds rather than one is what makes "start something completely
 * different" cheap - the alternative is asking a model to undo a fortnight of
 * accumulated ideas, which it does badly and which loses the fortnight.
 */
export function WorldsPage() {
  const worlds = useResource<WorldSummary[]>('game:worlds', getWorlds);
  const [name, setName] = useState('');
  const [icon, setIcon] = useState(ICONS[0]);
  const create = useMutation();
  const remove = useMutation();
  const navigate = useNavigate();

  if (worlds.loading && !worlds.data) return <Loading />;
  if (worlds.error) return <ErrorNote message={worlds.error} onRetry={worlds.reload} />;

  const rows = worlds.data ?? [];

  return (
    <div className="game-worlds">
      <div className="game-world-cards">
        {rows.map((world) => (
          <div key={world.id} className="card game-world-card">
            <Link to={worldPath(world.id)} className="game-world-link">
              <span className="game-world-icon" aria-hidden="true">
                {world.icon ?? '🎮'}
              </span>
              <span className="game-world-text">
                <span className="game-world-title">{world.name}</span>
                <span className="game-world-sub">{describe(world)}</span>
              </span>
            </Link>
            <button
              type="button"
              className="game-world-delete"
              aria-label={`Delete ${world.name}`}
              disabled={remove.busy}
              onClick={() => {
                // A game is an afternoon's work and the delete button is next
                // to a thumb that plays here.
                if (!confirm(`Delete "${world.name}" and everything in it?`)) return;
                void remove.run(() => deleteWorld(world.id)).then((ok) => ok && worlds.reload());
              }}
            >
              🗑
            </button>
          </div>
        ))}
      </div>

      {remove.error && <InlineError message={remove.error} />}

      <form
        className="game-new"
        onSubmit={(event) => {
          event.preventDefault();
          const trimmed = name.trim();
          if (trimmed.length === 0) return;
          void create.run(async () => {
            const world = await createWorld(trimmed, icon);
            navigate(worldPath(world.world.id));
          });
        }}
      >
        <div className="game-new-icons" role="group" aria-label="Pick a picture">
          {ICONS.map((choice) => (
            <button
              key={choice}
              type="button"
              className={`game-new-icon${choice === icon ? ' active' : ''}`}
              onClick={() => setIcon(choice)}
            >
              {choice}
            </button>
          ))}
        </div>
        <div className="game-new-row">
          <input
            className="game-input"
            value={name}
            onChange={(event) => setName(event.target.value)}
            placeholder="Name a new game"
            aria-label="Name a new game"
            maxLength={60}
          />
          <button type="submit" className="game-go" disabled={create.busy || name.trim().length === 0}>
            Make it
          </button>
        </div>
        {create.error && <InlineError message={create.error} />}
      </form>
    </div>
  );
}

function describe(world: WorldSummary): string {
  // The last thing asked for says what a game *is* far better than its name
  // does - names get chosen once, and the game keeps going.
  if (world.lastPrompt) return `“${world.lastPrompt}”`;
  return world.versionCount > 1 ? `${world.versionCount} changes` : 'Brand new';
}

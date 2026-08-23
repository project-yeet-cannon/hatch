import { useCallback, useState } from 'react';
import { InlineError, Loading } from '../../components/Notices';
import { errorMessage, useResource } from '../../lib/useResource';
import { getVersions, revert } from './api';
import type { Version, World } from './types';

interface HistoryPanelProps {
  worldId: string;
  currentVersionId: string | null;
  /** Part of the resource key, so the list reloads itself after every turn. */
  versionCount: number;
  onClose: () => void;
  onRestored: (world: World) => void;
}

/**
 * Everything this game has ever been, newest first, with a way back to any of
 * it.
 *
 * This is the screen that makes experimenting safe, so it shows the whole
 * chain including the versions that broke - greyed out and unpickable. Hiding
 * them would be tidier and would also mean a parent looking for "the one before
 * it went wrong" has no landmark to count back from.
 */
export function HistoryPanel({ worldId, currentVersionId, versionCount, onClose, onRestored }: HistoryPanelProps) {
  const versions = useResource<Version[]>(`game:versions:${worldId}:${versionCount}`, (signal) =>
    getVersions(worldId, signal),
  );
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const goTo = useCallback(
    async (versionId: string) => {
      setBusy(versionId);
      setError(null);
      try {
        onRestored(await revert(worldId, versionId));
      } catch (err) {
        setError(errorMessage(err));
      } finally {
        setBusy(null);
      }
    },
    [onRestored, worldId],
  );

  return (
    <div className="game-history" role="dialog" aria-label="Everything we made">
      <div className="game-history-head">
        <span>Everything we made</span>
        <button type="button" className="game-history-close" onClick={onClose} aria-label="Close">
          ✕
        </button>
      </div>

      {versions.loading && !versions.data && <Loading />}
      {error && <InlineError message={error} />}

      <ol className="game-history-list">
        {(versions.data ?? []).map((version) => (
          <li
            key={version.id}
            className={`game-history-row${version.id === currentVersionId ? ' current' : ''}${version.isBroken ? ' broken' : ''}`}
          >
            <span className="game-history-mark" aria-hidden="true">
              {mark(version)}
            </span>
            <span className="game-history-text">
              <span className="game-history-title">{title(version)}</span>
              <span className="game-history-sub">{subtitle(version)}</span>
            </span>
            {version.id === currentVersionId ? (
              <span className="game-history-now">now</span>
            ) : (
              <button
                type="button"
                className="game-history-go"
                disabled={version.isBroken || busy !== null}
                onClick={() => void goTo(version.id)}
              >
                {busy === version.id ? '…' : 'Go back'}
              </button>
            )}
          </li>
        ))}
      </ol>
    </div>
  );
}

function mark(version: Version): string {
  if (version.isBroken) return '💥';
  switch (version.kind) {
    case 'Seed':
      return '🌱';
    case 'Repair':
      return '🔧';
    case 'Revert':
      return '↩';
    default:
      return '✨';
  }
}

/** What was asked for, falling back to what the model said it did. */
function title(version: Version): string {
  if (version.prompt) return version.prompt;
  return version.summary ?? 'The beginning';
}

/**
 * The second line is where the cost lives. It is here rather than on a settings
 * page because this is where someone is already asking "what did that do" - and
 * because a family running this on a home key deserves to see that a careful
 * turn costs more than a quick one without going looking.
 */
function subtitle(version: Version): string {
  const parts: string[] = [`#${version.ordinal}`];
  if (version.isBroken) parts.push('broke');
  if (version.durationMs > 0) parts.push(`${Math.round(version.durationMs / 100) / 10}s`);
  if (version.outputTokens > 0) parts.push(`${version.inputTokens + version.outputTokens} tokens`);
  if (version.model) parts.push(shortModel(version.model));
  return parts.join(' · ');
}

const shortModel = (model: string) => model.replace(/^claude-/, '').replace(/-\d{8}$/, '');

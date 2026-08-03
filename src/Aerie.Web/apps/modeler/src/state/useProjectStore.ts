import { useCallback, useEffect, useRef, useState } from 'react';
import { createEmptyProject } from '../model/schema';
import type { ProjectDocument } from '../model/schema';
import { loadActiveProject, saveActiveProject } from '../persistence/db';
import { canRedo, canUndo, createHistory, pushHistory, redo as redoHistory, undo as undoHistory } from '../persistence/history';
import type { HistoryState } from '../persistence/history';
import { downloadProject, readProjectFile } from '../persistence/projectFile';
import { clientLogger } from '../lib/clientLogger';

export type ProjectStoreStatus = 'loading' | 'ready';

export interface ProjectStore {
  status: ProjectStoreStatus;
  project: ProjectDocument | null;
  lastSavedAt: Date | null;
  isSaving: boolean;
  canUndo: boolean;
  canRedo: boolean;
  update(mutate: (project: ProjectDocument) => ProjectDocument): void;
  undo(): void;
  redo(): void;
  exportProject(): void;
  importProject(file: File): Promise<void>;
}

export function useProjectStore(): ProjectStore {
  const [status, setStatus] = useState<ProjectStoreStatus>('loading');
  const [history, setHistory] = useState<HistoryState<ProjectDocument> | null>(null);
  const [lastSavedAt, setLastSavedAt] = useState<Date | null>(null);
  const [isSaving, setIsSaving] = useState(false);
  // Guards against an in-flight save's .then() clobbering state after a
  // newer save has already started (IndexedDB writes aren't guaranteed to
  // resolve in call order once more than one is outstanding).
  const saveGeneration = useRef(0);

  useEffect(() => {
    let cancelled = false;
    loadActiveProject()
      .then((loaded) => {
        if (cancelled) return;
        const project = loaded ?? createEmptyProject();
        setHistory(createHistory(project));
        setStatus('ready');
        clientLogger.info('Project loaded', { projectId: project.id, fromStorage: loaded !== undefined });
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        clientLogger.error('Failed to load project from IndexedDB, starting a new one', {
          reason: err instanceof Error ? err.message : String(err),
        });
        setHistory(createHistory(createEmptyProject()));
        setStatus('ready');
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (!history) return;
    const generation = ++saveGeneration.current;
    setIsSaving(true);
    saveActiveProject(history.present)
      .then(() => {
        if (generation !== saveGeneration.current) return;
        setLastSavedAt(new Date());
      })
      .catch((err: unknown) => {
        clientLogger.error('Autosave failed', { reason: err instanceof Error ? err.message : String(err) });
      })
      .finally(() => {
        if (generation === saveGeneration.current) setIsSaving(false);
      });
  }, [history]);

  const update = useCallback((mutate: (project: ProjectDocument) => ProjectDocument) => {
    setHistory((current) => {
      if (!current) return current;
      const mutated = mutate(current.present);
      if (mutated === current.present) return current;
      const next: ProjectDocument = { ...mutated, updatedAt: new Date().toISOString() };
      return pushHistory(current, next);
    });
  }, []);

  const undo = useCallback(() => setHistory((current) => (current ? undoHistory(current) : current)), []);
  const redo = useCallback(() => setHistory((current) => (current ? redoHistory(current) : current)), []);

  const exportProject = useCallback(() => {
    if (!history) return;
    downloadProject(history.present);
  }, [history]);

  const importProject = useCallback(async (file: File) => {
    const imported = await readProjectFile(file);
    setHistory(createHistory(imported));
    clientLogger.info('Project imported', { projectId: imported.id });
  }, []);

  return {
    status,
    project: history?.present ?? null,
    lastSavedAt,
    isSaving,
    canUndo: history ? canUndo(history) : false,
    canRedo: history ? canRedo(history) : false,
    update,
    undo,
    redo,
    exportProject,
    importProject,
  };
}

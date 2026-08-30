import { describe, expect, it } from 'vitest';
import { createMirror, memoryStore } from './store';
import type { KeyValueStore } from './store';
import type { Note } from './types';

const ADA = 'ada-0000-0000-0000-000000000000';
const BEN = 'ben-0000-0000-0000-000000000000';

const note = (id: string): Note => ({
  id,
  title: id,
  body: `body of ${id}`,
  createdAt: '2026-08-30T09:00:00Z',
  updatedAt: '2026-08-30T09:00:00Z',
});

describe('the offline mirror', () => {
  it('reads back what it wrote', async () => {
    const mirror = createMirror(memoryStore());

    await mirror.write(ADA, [note('one'), note('two')]);

    expect((await mirror.read(ADA))?.map((n) => n.id)).toEqual(['one', 'two']);
  });

  it('has nothing before anything has been written', async () => {
    expect(await createMirror(memoryStore()).read(ADA)).toBeNull();
  });

  it('carries whole notes, not summaries', async () => {
    // The property the list endpoint is shaped for: a note nobody re-opened is
    // still readable on a plane.
    const mirror = createMirror(memoryStore());
    await mirror.write(ADA, [note('monday')]);

    expect((await mirror.read(ADA))?.[0].body).toBe('body of monday');
  });

  /**
   * A device can be re-linked to someone else on the admin Sessions page. A
   * mirror that only held notes would hand the previous owner's notes to the
   * new one the first time the API was unreachable - which is the exact failure
   * the mirror exists to prevent, arriving through the mirror.
   */
  it('will not answer with someone else’s notes', async () => {
    const mirror = createMirror(memoryStore());
    await mirror.write(ADA, [note('hers')]);

    expect(await mirror.read(BEN)).toBeNull();
  });

  it('throws the other person’s notes away rather than leaving them for later', async () => {
    const store = memoryStore();
    const mirror = createMirror(store);
    await mirror.write(ADA, [note('hers')]);

    await mirror.read(BEN);

    // Not just filtered on read: gone. Offline, a "next successful sync" that
    // would have cleared them may never come.
    expect(await store.get('notes')).toBeNull();
  });

  it('replaces rather than merges, so a note deleted elsewhere does not come back', async () => {
    const mirror = createMirror(memoryStore());
    await mirror.write(ADA, [note('one'), note('two')]);

    await mirror.write(ADA, [note('one')]);

    expect((await mirror.read(ADA))?.map((n) => n.id)).toEqual(['one']);
  });

  it('is emptied by clear', async () => {
    const mirror = createMirror(memoryStore());
    await mirror.write(ADA, [note('one')]);

    await mirror.clear();

    expect(await mirror.read(ADA)).toBeNull();
  });

  it('treats a snapshot from an older bundle as nothing rather than crashing the list', async () => {
    const store = memoryStore();
    await store.set('notes', { personId: ADA, savedAt: 'whenever' });

    expect(await createMirror(store).read(ADA)).toBeNull();
  });

  it('lets a store failure surface, so the caller can fall back to the network', async () => {
    const broken: KeyValueStore = {
      get: () => Promise.reject(new Error('quota')),
      set: () => Promise.reject(new Error('quota')),
      remove: () => Promise.resolve(),
    };

    await expect(createMirror(broken).read(ADA)).rejects.toThrow('quota');
  });
});

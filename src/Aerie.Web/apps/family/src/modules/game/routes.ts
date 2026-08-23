/**
 * Where this module is mounted in the shell - the `id` of its entry in
 * src/modules/registry.ts. Same reasoning as Gather's routes.ts: links are
 * built from an absolute, shell-relative path rather than with `..`, which
 * means two different things depending on whether an index route or a child
 * route is doing the linking.
 */
const BASE = '/game';

/** The play screen. No world id: it opens whatever was played last. */
export const playPath = BASE;

export const worldsPath = `${BASE}/worlds`;

export const worldPath = (id: string) => `${BASE}/w/${id}`;

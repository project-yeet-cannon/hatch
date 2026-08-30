/**
 * Where this module is mounted in the shell - the `id` of its entry in
 * src/modules/registry.ts, and the one place that fact is written down inside
 * the module.
 *
 * Links are built from here rather than with `../`, because `..` resolves
 * against the *route* hierarchy and this module renders links from an index
 * route and from ordinary routes alike, where it means two different things.
 */
const BASE = '/quill';

export const notesPath = BASE;

/**
 * The editor for a note that does not exist yet. It becomes a real note - and
 * this URL is replaced with the one below - at the first save with something in
 * it, so backing out of an empty note leaves nothing behind.
 */
export const newNotePath = `${BASE}/new`;

export const notePath = (id: string) => `${BASE}/${id}`;

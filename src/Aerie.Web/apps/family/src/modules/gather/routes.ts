/**
 * Where this module is mounted in the shell - the `id` of its entry in
 * src/modules/registry.ts, and the one place that fact is written down inside
 * the module.
 *
 * Links are built from here rather than with `../`, because `..` resolves
 * against the *route* hierarchy and this module renders links from an index
 * route and from ordinary routes alike, where it means two different things.
 * These paths are absolute but still shell-relative: the router applies the
 * /apps/family basename from main.tsx.
 */
const BASE = '/gather';

export const listsPath = BASE;

export const listPath = (id: string) => `${BASE}/${id}`;

/**
 * Where this module is mounted in the shell - the `id` of its entry in
 * src/modules/registry.ts, and the one place that fact is written down inside
 * the module.
 *
 * Links are built from here rather than with `../`, because `..` resolves
 * against the *route* hierarchy and this module renders links from an index
 * route, a splat route and ordinary routes alike, where it means three
 * different things. These paths are absolute but still shell-relative: the
 * router applies the /apps/family basename from main.tsx.
 */
const BASE = '/storage';

export const itemsPath = `${BASE}/items`;
export const cratesPath = `${BASE}/crates`;
export const locationsPath = `${BASE}/locations`;

export const cratePath = (id: string) => `${cratesPath}/${id}`;

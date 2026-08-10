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
export const labelsPath = `${BASE}/labels`;

export const cratePath = (id: string) => `${cratesPath}/${id}`;

/** The labels screen, preloaded with crates to reprint (a faded label, a re-taped box). */
export const reprintPath = (codes: string[]) =>
  `${labelsPath}?${new URLSearchParams(codes.map((code) => ['code', code]))}`;

/**
 * What a QR label encodes, and the one URL here that is absolute.
 *
 * `base` is the install's canonical origin (Apps:PublicBaseUrl, via the shell's
 * appConfig) rather than `window.location.origin`, because this string is
 * printed onto tape and outlives the machine that printed it. The `/apps/family`
 * prefix is spelled out for the same reason: the router's basename is a runtime
 * fact, and this is paper.
 *
 * The code goes in bare and undashed - the dash is presentation, and
 * CrateCode.Normalize on the server takes either.
 */
export const crateLabelUrl = (base: string, code: string) => `${base}/apps/family${BASE}/c/${code}`;

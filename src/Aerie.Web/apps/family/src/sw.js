/* eslint-env serviceworker */
//
// Service worker source. Not imported by the app and not processed by Vite -
// `serviceWorker()` in vite.config.ts substitutes the three build-time
// placeholders below (cache name, precache list, base path) and emits the
// result as sw.js at the bundle root, so its scope is the whole family shell
// and nothing above it.
//
// Policy, per TODO_APPS Phase 2: cache-first for the shell, network-first for
// data. An offline *read* of a crate you've already opened is useful in a
// garage with no signal; an offline write isn't worth the sync complexity yet,
// so writes simply fail as they would without a worker at all.

const CACHE = 'aerie-family-__BUILD_ID__';
const DATA_CACHE = 'aerie-family-data';
const SHELL = '__BASE__index.html';
const PRECACHE = __PRECACHE__;

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(PRECACHE)));
});

// Deliberately no skipWaiting(). A new build empties the output folder, so the
// hashed assets the running page was loaded against no longer exist on the
// server - its only remaining copy is the previous cache. Activating early
// would delete that cache out from under it, and the failure this produces is
// nasty and delayed: the shell keeps working, then tapping into a module fetches
// a lazily-loaded chunk that is gone from both cache and server.
//
// So the new worker waits until no page is using the old one. That costs one
// launch of update latency and buys a page that is never half-upgraded. It
// works because PRECACHE covers the lazy chunks too, not just the entry bundle,
// which is what lets the outgoing cache stay self-sufficient to the last.
self.addEventListener('activate', (event) => {
  event.waitUntil(
    (async () => {
      const names = await caches.keys();
      await Promise.all(
        names
          .filter((name) => name.startsWith('aerie-family-') && name !== CACHE && name !== DATA_CACHE)
          .map((name) => caches.delete(name)),
      );
      // Only reached with no controlled clients, except on a first-ever install
      // - which is exactly the case worth claiming, so the page that just
      // registered the worker is cached rather than the one after it.
      await self.clients.claim();
    })(),
  );
});

/** Network-first, falling back to the last good response. Data may be stale; it may not be absent. */
async function dataFirst(request) {
  const cache = await caches.open(DATA_CACHE);
  try {
    const response = await fetch(request);
    if (response.ok) cache.put(request, response.clone());
    return response;
  } catch (err) {
    const cached = await cache.match(request);
    if (cached) return cached;
    throw err;
  }
}

/** Cache-first. Everything routed here is either precached or content-hashed, so staleness isn't possible. */
async function shellFirst(request) {
  const cache = await caches.open(CACHE);
  const cached = await cache.match(request);
  if (cached) return cached;

  const response = await fetch(request);
  if (response.ok) cache.put(request, response.clone());
  return response;
}

/** The shell document, for any client-side route. */
async function shellDocument(request) {
  const cache = await caches.open(CACHE);
  return (await cache.match(SHELL)) ?? fetch(request);
}

self.addEventListener('fetch', (event) => {
  const { request } = event;
  if (request.method !== 'GET') return;

  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;

  // Every client-side route resolves to the one shell document, which is what
  // makes a scanned QR (/apps/family/storage/c/ABC-123) open offline too.
  if (request.mode === 'navigate') {
    event.respondWith(shellDocument(request));
    return;
  }

  if (url.pathname.startsWith('/api/')) {
    event.respondWith(dataFirst(request));
    return;
  }

  if (url.pathname.startsWith('__BASE__')) {
    event.respondWith(shellFirst(request));
  }
});

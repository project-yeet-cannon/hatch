import { clientLogger } from './clientLogger';

/**
 * Registers the worker emitted by vite.config.ts's `serviceWorker()` plugin.
 *
 * Dev-server only serves what Vite builds, and that plugin is build-only, so
 * registering in dev would 404 - and a worker registered against unhashed dev
 * modules would be actively harmful anyway.
 */
export function registerServiceWorker(): void {
  if (!import.meta.env.PROD || !('serviceWorker' in navigator)) return;

  window.addEventListener('load', () => {
    navigator.serviceWorker
      // updateViaCache: 'none' so the browser always revalidates sw.js itself.
      // Everything the worker precaches is content-hashed, but sw.js isn't -
      // it's the one file whose staleness would pin the app to an old deploy.
      .register('/apps/family/sw.js', { scope: '/apps/family/', updateViaCache: 'none' })
      .then((registration) => {
        clientLogger.info('service worker registered', { scope: registration.scope });
      })
      .catch((error: unknown) => {
        // Not fatal - the app works online without it, it just won't survive
        // airplane mode. Worth a log line rather than a silent downgrade.
        clientLogger.warn('service worker registration failed', {
          reason: error instanceof Error ? error.message : String(error),
        });
      });
  });
}

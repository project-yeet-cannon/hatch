import { useSyncExternalStore } from 'react';

function subscribe(onChange: () => void): () => void {
  window.addEventListener('online', onChange);
  window.addEventListener('offline', onChange);
  return () => {
    window.removeEventListener('online', onChange);
    window.removeEventListener('offline', onChange);
  };
}

/**
 * navigator.onLine only reports whether the device has *a* network, not whether
 * Aerie is reachable - on a phone that has wandered off the tailnet it will
 * happily say true. It's still worth showing, because the case it does catch
 * (no wifi in the far corner of the garage) is the common one, and the
 * alternative is a save button that fails with no explanation.
 */
export function useOnline(): boolean {
  return useSyncExternalStore(
    subscribe,
    () => navigator.onLine,
    () => true,
  );
}

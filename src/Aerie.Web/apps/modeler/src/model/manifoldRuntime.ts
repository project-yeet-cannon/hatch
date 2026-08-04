/// <reference types="node" />
// The node: imports below are only ever reached in the vitest/Node branch (see loadWasmBinaryForNode) -
// this reference just satisfies the typechecker for that dynamic import, not a runtime browser dependency.
import Module from 'manifold-3d';
import type { ManifoldToplevel } from 'manifold-3d';
import { cleanup, garbageCollectManifold } from 'manifold-3d/lib/garbage-collector';
import wasmUrl from 'manifold-3d/manifold.wasm?url';

let modulePromise: Promise<ManifoldToplevel> | null = null;

/**
 * In the browser (dev server or built app), `wasmUrl` is a real fetchable
 * URL and `locateFile` is all Module() needs. Under vitest there's no dev
 * server serving it - Vite's `?url` transform still resolves to a
 * server-style path (e.g. `/node_modules/manifold-3d/manifold.wasm`), which
 * doesn't exist on disk - so read the real file directly instead.
 */
async function loadWasmBinaryForNode(): Promise<Uint8Array | undefined> {
  if (typeof window !== 'undefined') return undefined;
  const { readFile } = await import('node:fs/promises');
  const { fileURLToPath } = await import('node:url');
  const wasmPath = fileURLToPath(import.meta.resolve('manifold-3d/manifold.wasm'));
  return readFile(wasmPath);
}

/**
 * Loads the manifold-3d WASM module once and caches it. The returned module
 * is wrapped so every Manifold/CrossSection produced by its tracked factory
 * methods (extrude, union, subtract, ofMesh, translate, ...) is registered
 * for cleanup - the WASM heap those objects live on isn't touched by the JS
 * garbage collector, so without this every 3D regeneration would leak.
 * Objects created via `new Manifold(...)` / `new CrossSection(...)` directly
 * are NOT tracked; solidGeneration.ts sticks to the tracked static factories
 * for exactly this reason.
 */
export function getManifoldModule(): Promise<ManifoldToplevel> {
  if (!modulePromise) {
    modulePromise = (async () => {
      // Module()'s declared config type is intentionally just {locateFile},
      // but the underlying Emscripten glue also honors wasmBinary to skip
      // fetching entirely - build the config through a variable (rather than
      // an object literal) so TS's excess-property check doesn't reject it.
      const config: { locateFile: () => string; wasmBinary?: Uint8Array } = { locateFile: () => wasmUrl };
      const wasmBinary = await loadWasmBinaryForNode();
      if (wasmBinary) config.wasmBinary = wasmBinary;

      const wasm = await Module(config);
      wasm.setup();
      return garbageCollectManifold(wasm);
    })();
  }
  return modulePromise;
}

/** Frees every tracked Manifold/CrossSection created since the last call. Call once after extracting plain-JS mesh data from a generation pass (in a finally, so a mid-generation throw still cleans up). */
export function cleanupManifoldObjects(): void {
  cleanup();
}

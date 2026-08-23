import engineSource from './runtime/engine.js?raw';
import hostSource from './runtime/host.js?raw';

/**
 * The document the game runs inside.
 *
 * The engine and the host are inlined as text rather than loaded by URL, and
 * that is not laziness - the frame is sandboxed without `allow-same-origin`, so
 * it has an opaque origin: a `<script type="module">` would be a cross-origin
 * fetch that CORS refuses, and `import()` of a blob: URL minted in an opaque
 * origin is refused outright. Inlining sidesteps all of it, and has the happy
 * side effect that the frame needs no network at all to start.
 *
 * Built once at module load. It never varies - the game itself arrives later by
 * postMessage - which means React can hand the same `srcDoc` to the iframe for
 * the life of the app and never reload it.
 */
export const frameDocument = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
<style>
  html, body { margin: 0; padding: 0; height: 100%; overflow: hidden; background: #7ec8f5; }
  /* touch-action so dragging the ball on a tablet doesn't scroll the page out
     from under the game instead. */
  canvas { display: block; width: 100%; height: 100%; touch-action: none; }
</style>
</head>
<body>
<canvas id="stage"></canvas>
<script>${engineSource}</script>
<script>${hostSource}</script>
</body>
</html>`;

/*
  Runs engine.js outside a browser, for engine.test.js.

  The engine is a classic script that hangs itself off `window` and draws onto a
  canvas - neither of which exists under a test runner. Rather than reshape the
  engine to suit its test (it has to stay a plain script the sandboxed frame can
  inline), this evaluates the same file with a stand-in for both.

  The canvas stub swallows drawing and answers questions about size. That is
  enough: nothing in the engine's behaviour depends on what the pixels came out
  like, and a test that asserted on draw calls would fail every time a colour
  changed.
*/
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

/** A 2D context that accepts every call and returns something plausible. */
function stubContext() {
  const gradient = { addColorStop() {} };
  return new Proxy(
    {},
    {
      get: (target, key) => {
        if (key in target) return target[key];
        return () => (key === 'createLinearGradient' ? gradient : undefined);
      },
      set: (target, key, value) => {
        target[key] = value;
        return true;
      },
    },
  );
}

export function stubCanvas(width = 900, height = 600) {
  const listeners = {};
  return {
    clientWidth: width,
    clientHeight: height,
    width,
    height,
    style: {},
    getContext: () => stubContext(),
    getBoundingClientRect: () => ({ left: 0, top: 0, width, height }),
    setPointerCapture() {},
    addEventListener(type, handler) {
      (listeners[type] ??= []).push(handler);
    },
    /** Lets a test play the browser and deliver an event the engine subscribed to. */
    emit(type, event) {
      for (const handler of listeners[type] ?? []) handler(event);
    },
  };
}

/** Loads a fresh copy of the engine, so no test can leak state into the next. */
export function loadEngine() {
  const source = readFileSync(join(here, 'engine.js'), 'utf8');
  const windowStub = {
    devicePixelRatio: 1,
    addEventListener() {},
    // No AudioContext: the engine has to degrade to silence rather than throw,
    // which is also what a browser with audio blocked looks like.
    AudioContext: undefined,
  };

  new Function('window', source)(windowStub);
  return windowStub.AerieEngine;
}

/**
 * Runs game code the way the sandboxed host does - `new Function`, a
 * `defineGame` callback - and returns the world plus a way to advance it.
 */
export function runGame(code, options = {}) {
  const engine = loadEngine();
  const canvas = options.canvas ?? stubCanvas();

  let definition = null;
  new Function('defineGame', code)((value) => {
    definition = value;
  });
  if (!definition?.setup) throw new Error('The code never called defineGame({ setup }).');

  const instance = engine.createWorld(canvas, options);
  definition.setup(instance.world);

  return {
    world: instance.world,
    canvas,
    records: instance.records,
    /** Advances by `seconds` in 60fps steps, running update and draw like the host does. */
    run(seconds, step = 1 / 60) {
      for (let elapsed = 0; elapsed < seconds; elapsed += step) {
        instance.step(step);
        definition.update?.(instance.world, step);
        instance.draw();
      }
    },
  };
}

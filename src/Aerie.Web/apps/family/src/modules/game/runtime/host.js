/*
 * The runtime host, running inside the sandboxed game frame.
 *
 * The frame is created with `sandbox="allow-scripts"` and nothing else, which
 * means it has an opaque origin: no cookies, no same-origin fetch, no
 * localStorage, no reach into the family shell. Generated code cannot touch
 * Aerie's API even by accident, which is the point - this is the one place in
 * the house that runs code nobody reviewed.
 *
 * Everything therefore crosses by postMessage:
 *
 *   parent -> frame   load (code + records), pause, resume
 *   frame  -> parent  ready, error, records
 *
 * Persisting records is the parent's job for the same reason: an opaque origin
 * has no storage of its own to keep them in.
 */
(function () {
  'use strict';

  const canvas = document.getElementById('stage');
  const send = (message) => {
    // An opaque origin cannot name its parent's origin, so '*' is the only
    // option here. It is safe in this direction: nothing sent up is secret,
    // and the parent verifies the frame it came from.
    parent.postMessage(message, '*');
  };

  let running = null;
  let paused = false;
  let lastFrame = 0;
  let frameHandle = null;
  /** Set once a version has failed, so a broken update loop reports once and stops. */
  let failed = false;

  function fit() {
    const ratio = window.devicePixelRatio || 1;
    canvas.width = Math.floor(window.innerWidth * ratio);
    canvas.height = Math.floor(window.innerHeight * ratio);
    canvas.style.width = `${window.innerWidth}px`;
    canvas.style.height = `${window.innerHeight}px`;
    const ctx = canvas.getContext('2d');
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
  }

  function describe(err) {
    if (err instanceof Error) {
      return { message: `${err.name}: ${err.message}`, stack: String(err.stack || '').split('\n').slice(0, 4).join('\n') };
    }
    return { message: String(err), stack: '' };
  }

  function report(phase, err) {
    const detail = describe(err);
    send({ type: 'aerie:error', phase, message: detail.message, stack: detail.stack });
  }

  /**
   * A failure inside the loop stops the loop. Left running it would repaint the
   * same exception sixty times a second, which floods the repair request with
   * duplicates and pins the tablet's CPU while a child watches a frozen screen.
   */
  function fail(phase, err) {
    if (failed) return;
    failed = true;
    stopLoop();
    report(phase, err);
  }

  function stopLoop() {
    if (frameHandle !== null) {
      cancelAnimationFrame(frameHandle);
      frameHandle = null;
    }
  }

  function frame(now) {
    frameHandle = requestAnimationFrame(frame);
    if (!running || paused) {
      lastFrame = now;
      return;
    }
    const dt = lastFrame ? (now - lastFrame) / 1000 : 0;
    lastFrame = now;
    if (dt <= 0) return;

    try {
      running.step(dt);
      if (running.definition.update) running.definition.update(running.world, dt);
    } catch (err) {
      fail('update', err);
      return;
    }

    try {
      running.draw();
    } catch (err) {
      fail('draw', err);
    }
  }

  function load(code, records) {
    stopLoop();
    if (running) {
      try {
        running.dispose();
      } catch {
        // The version being replaced is already on its way out.
      }
    }
    running = null;
    failed = false;
    lastFrame = 0;

    let definition = null;
    const defineGame = (value) => {
      definition = value;
    };

    // `new Function` rather than a module import: a blob: URL minted inside an
    // opaque origin is not importable, and this is the plainest dynamic
    // evaluation that works in a sandbox with only allow-scripts. It is also
    // exactly the "live code, no rebuild" mechanism the whole app is for.
    let build;
    try {
      build = new Function('defineGame', 'AerieEngine', `"use strict";\n${code}`);
    } catch (err) {
      report('compile', err);
      return;
    }

    try {
      build(defineGame, window.AerieEngine);
    } catch (err) {
      report('compile', err);
      return;
    }

    if (!definition || typeof definition.setup !== 'function') {
      report('compile', new Error('The game never called defineGame({ setup }).'));
      return;
    }

    const instance = window.AerieEngine.createWorld(canvas, {
      records: records || {},
      onError: (err) => fail('update', err),
      onRecord: (next) => send({ type: 'aerie:records', records: next }),
    });

    try {
      definition.setup(instance.world);
    } catch (err) {
      report('setup', err);
      return;
    }

    running = {
      definition,
      world: instance.world,
      step: instance.step,
      draw: instance.draw,
      dispose: instance.dispose,
    };

    try {
      instance.draw();
    } catch (err) {
      report('draw', err);
      return;
    }

    send({ type: 'aerie:ready' });
    frameHandle = requestAnimationFrame(frame);
  }

  window.addEventListener('message', (event) => {
    if (event.source !== parent) return;
    const data = event.data;
    if (!data || typeof data.type !== 'string') return;

    if (data.type === 'aerie:load') load(String(data.code || ''), data.records);
    else if (data.type === 'aerie:pause') paused = true;
    else if (data.type === 'aerie:resume') {
      paused = false;
      lastFrame = 0;
    }
  });

  // Anything thrown outside a call the host made itself - a setTimeout the game
  // scheduled, a rejected promise - still has to become a repairable report
  // rather than a silent freeze.
  window.addEventListener('error', (event) => {
    fail('update', event.error || new Error(event.message));
  });
  window.addEventListener('unhandledrejection', (event) => {
    fail('update', event.reason || new Error('Unhandled promise rejection'));
  });

  window.addEventListener('resize', fit);
  document.addEventListener('visibilitychange', () => {
    // Coming back from a backgrounded tab otherwise integrates one enormous
    // step and teleports everything through the floor.
    if (!document.hidden) lastFrame = 0;
  });

  fit();
  send({ type: 'aerie:hello' });
})();

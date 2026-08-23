/*
 * The Aerie game engine.
 *
 * This file is not part of the family shell's bundle in the usual sense: it is
 * imported as raw text (see frame.ts) and injected into a sandboxed iframe as a
 * classic script, alongside whatever game code Claude last wrote. It therefore
 * has to be plain ES2020-ish JavaScript with no imports and no build step.
 *
 * Why an engine at all, rather than letting the model write a game from
 * scratch each turn: everything below is the part that is the same in every
 * game a four-year-old asks for - a loop, gravity, a ground to stand on, keys
 * and touch, a way to keep score. Holding it still means a turn is fifty lines
 * of intent instead of six hundred lines of boilerplate, which is the whole
 * difference between a change landing in twenty seconds and landing in two
 * minutes with a new bug in the physics.
 *
 * The API surface this exposes is mirrored, in prose, in
 * Modules/Game/GameEngineReference.cs on the server - that text is the system
 * prompt. A method added here and not written down there does not exist as far
 * as the model is concerned. GameEngineReferenceTests holds the two together.
 */
(function (global) {
  'use strict';

  /** Screen pixels to a "meter", so distances announced to a player are human-sized. */
  const PIXELS_PER_METER = 50;

  /** Physics runs at a fixed step; frames longer than this are dropped rather than integrated. */
  const MAX_STEP = 1 / 30;

  const DEFAULT_GRAVITY = 1600;

  // ---------------------------------------------------------------- utilities

  const clamp = (value, low, high) => (value < low ? low : value > high ? high : value);
  const lerp = (a, b, t) => a + (b - a) * t;

  function noop() {}

  /** Calls every handler, letting one bad one fail without taking the rest down. */
  function fire(handlers, args, onError) {
    for (let i = 0; i < handlers.length; i++) {
      try {
        handlers[i].apply(null, args);
      } catch (err) {
        onError(err);
      }
    }
  }

  // ------------------------------------------------------------------- sounds

  /**
   * Four synthesised blips, because a game with no sound reads as broken to a
   * child and shipping audio files through a sandboxed frame does not.
   * Browsers refuse to start audio before a gesture, so the context is created
   * on the first key or tap and every call before that is silently dropped.
   */
  function createSound() {
    let ctx = null;

    function ensure() {
      if (ctx) return ctx;
      const Ctor = global.AudioContext || global.webkitAudioContext;
      if (!Ctor) return null;
      ctx = new Ctor();
      return ctx;
    }

    function tone(freq, endFreq, seconds, type, gain) {
      const audio = ensure();
      if (!audio || audio.state === 'suspended') {
        if (audio) audio.resume().catch(noop);
        if (!audio) return;
      }
      const now = audio.currentTime;
      const osc = audio.createOscillator();
      const amp = audio.createGain();
      osc.type = type;
      osc.frequency.setValueAtTime(freq, now);
      if (endFreq !== freq) osc.frequency.exponentialRampToValueAtTime(Math.max(40, endFreq), now + seconds);
      amp.gain.setValueAtTime(gain, now);
      amp.gain.exponentialRampToValueAtTime(0.0001, now + seconds);
      osc.connect(amp).connect(audio.destination);
      osc.start(now);
      osc.stop(now + seconds + 0.02);
    }

    const voices = {
      boing: () => tone(180, 620, 0.18, 'sine', 0.18),
      jump: () => tone(300, 720, 0.12, 'square', 0.10),
      coin: () => {
        tone(880, 880, 0.07, 'square', 0.10);
        setTimeout(() => tone(1320, 1320, 0.12, 'square', 0.10), 70);
      },
      whoosh: () => tone(700, 120, 0.22, 'sawtooth', 0.07),
      sad: () => tone(420, 110, 0.45, 'triangle', 0.16),
      win: () => {
        [523, 659, 784, 1047].forEach((f, i) => setTimeout(() => tone(f, f, 0.14, 'square', 0.12), i * 90));
      },
      pop: () => tone(520, 240, 0.09, 'triangle', 0.12),
    };

    return {
      play(name) {
        const voice = voices[name] || voices.pop;
        try {
          voice();
        } catch {
          // An audio failure is never worth ending a game over.
        }
      },
      unlock() {
        const audio = ensure();
        if (audio && audio.state === 'suspended') audio.resume().catch(noop);
      },
    };
  }

  // ------------------------------------------------------------------ terrain

  /**
   * The ground is a single left-to-right profile - one polyline, kept sorted by
   * x - rather than a set of independent surfaces.
   *
   * That is a real constraint and it is deliberate. With one profile, "what is
   * the ground under this ball" has exactly one answer at every x, so rolling,
   * slope acceleration and landing detection are all a lookup; with a soup of
   * overlapping platforms it becomes a search with ambiguous answers, and the
   * failure mode is a ball that falls through a hill it was just sitting on.
   * Platforms, walls and floating islands are static bodies instead, which
   * collide as boxes and never lie about which surface you are on.
   */
  function createTerrain() {
    let points = [];
    let style = { color: '#4caf50', soil: '#6d4c41' };

    function sortPoints() {
      points.sort((a, b) => a[0] - b[0]);
    }

    return {
      get points() {
        return points;
      },
      get style() {
        return style;
      },
      get isEmpty() {
        return points.length < 2;
      },
      setStyle(next) {
        style = Object.assign({}, style, next);
      },
      add(newPoints) {
        for (let i = 0; i < newPoints.length; i++) {
          const p = newPoints[i];
          const x = Number(p[0]);
          const y = Number(p[1]);
          if (Number.isFinite(x) && Number.isFinite(y)) points.push([x, y]);
        }
        sortPoints();
      },
      clear() {
        points = [];
      },
      /** Surface height at x, or null where there is no ground at all (a gap past either end). */
      surfaceAt(x) {
        if (points.length < 2) return null;
        if (x < points[0][0] || x > points[points.length - 1][0]) return null;
        // Linear scan: profiles here are tens of points, and a binary search
        // would be a micro-optimisation on a cost nothing has ever noticed.
        for (let i = 0; i < points.length - 1; i++) {
          const a = points[i];
          const b = points[i + 1];
          if (x >= a[0] && x <= b[0]) {
            const span = b[0] - a[0];
            const t = span === 0 ? 0 : (x - a[0]) / span;
            return lerp(a[1], b[1], t);
          }
        }
        return points[points.length - 1][1];
      },
      /** Downhill direction and steepness at x, as a unit tangent pointing right. */
      tangentAt(x) {
        if (points.length < 2) return { tx: 1, ty: 0 };
        for (let i = 0; i < points.length - 1; i++) {
          const a = points[i];
          const b = points[i + 1];
          if (x >= a[0] && x <= b[0]) {
            const dx = b[0] - a[0];
            const dy = b[1] - a[1];
            const len = Math.hypot(dx, dy) || 1;
            return { tx: dx / len, ty: dy / len };
          }
        }
        return { tx: 1, ty: 0 };
      },
      bounds() {
        if (points.length === 0) return null;
        return { left: points[0][0], right: points[points.length - 1][0] };
      },
    };
  }

  // -------------------------------------------------------------------- input

  const KEY_ALIASES = {
    ArrowLeft: 'left',
    ArrowRight: 'right',
    ArrowUp: 'up',
    ArrowDown: 'down',
    KeyA: 'left',
    KeyD: 'right',
    KeyW: 'up',
    KeyS: 'down',
    Space: 'space',
    Enter: 'enter',
    ShiftLeft: 'shift',
    ShiftRight: 'shift',
  };

  function createInput(canvas, sound, onError) {
    const keys = { left: false, right: false, up: false, down: false, space: false, enter: false, shift: false };
    const keyHandlers = {};
    const tapHandlers = [];
    const pointer = { x: 0, y: 0, down: false, worldX: 0, worldY: 0 };
    /** Half-screen touch controls, synthesised into the same `keys` the model reads. */
    const touch = { active: false, moveId: null, moveStartX: 0, moveX: 0 };

    function named(event) {
      return KEY_ALIASES[event.code] || null;
    }

    function press(name) {
      if (!name) return;
      keys[name] = true;
      const handlers = keyHandlers[name];
      if (handlers) fire(handlers, [], onError);
    }

    global.addEventListener('keydown', (event) => {
      sound.unlock();
      const name = named(event);
      if (!name) return;
      // Space and the arrows scroll the page otherwise, which in a game frame
      // reads as the controls randomly not working.
      event.preventDefault();
      if (!keys[name]) press(name);
    });

    global.addEventListener('keyup', (event) => {
      const name = named(event);
      if (name) keys[name] = false;
    });

    // Losing focus mid-hold otherwise leaves a key stuck down forever.
    global.addEventListener('blur', () => {
      Object.keys(keys).forEach((k) => {
        keys[k] = false;
      });
      touch.moveId = null;
      touch.moveX = touch.moveStartX;
    });

    function localPoint(event) {
      const rect = canvas.getBoundingClientRect();
      return { x: event.clientX - rect.left, y: event.clientY - rect.top };
    }

    canvas.addEventListener('pointerdown', (event) => {
      sound.unlock();
      canvas.setPointerCapture(event.pointerId);
      const p = localPoint(event);
      pointer.x = p.x;
      pointer.y = p.y;
      pointer.down = true;

      if (event.pointerType === 'touch') {
        touch.active = true;
        // Left half steers, right half jumps: a d-pad drawn on a tablet is a
        // thing to miss, and half the screen is not.
        if (p.x < canvas.clientWidth / 2) {
          touch.moveId = event.pointerId;
          touch.moveStartX = p.x;
          touch.moveX = p.x;
        } else {
          press('up');
          press('space');
          setTimeout(() => {
            keys.up = false;
            keys.space = false;
          }, 90);
        }
      }
      fire(tapHandlers, [pointer.worldX, pointer.worldY], onError);
    });

    canvas.addEventListener('pointermove', (event) => {
      const p = localPoint(event);
      pointer.x = p.x;
      pointer.y = p.y;
      if (touch.moveId === event.pointerId) touch.moveX = p.x;
    });

    function release(event) {
      if (event.pointerId === touch.moveId) {
        touch.moveId = null;
        keys.left = false;
        keys.right = false;
      }
      pointer.down = false;
    }

    canvas.addEventListener('pointerup', release);
    canvas.addEventListener('pointercancel', release);

    return {
      keys,
      pointer,
      touch,
      onKey(name, handler) {
        const list = keyHandlers[name] || (keyHandlers[name] = []);
        list.push(handler);
      },
      onTap(handler) {
        tapHandlers.push(handler);
      },
      /** Folds the touch steering into `keys` so game code only ever reads keys. */
      applyTouchSteering() {
        if (touch.moveId === null) return;
        const drag = touch.moveX - touch.moveStartX;
        keys.left = drag < -12;
        keys.right = drag > 12;
      },
    };
  }

  // -------------------------------------------------------------------- world

  function createWorld(canvas, options) {
    const ctx = canvas.getContext('2d');
    const sound = createSound();
    const terrain = createTerrain();
    const opts = options || {};
    const onError = opts.onError || noop;
    const onRecord = opts.onRecord || noop;

    const input = createInput(canvas, sound, onError);

    const bodies = [];
    const timers = [];
    const hitRules = [];
    const particles = [];
    const records = {};
    const seeded = opts.records || {};
    Object.keys(seeded).forEach((id) => {
      records[id] = Object.assign({}, seeded[id]);
    });

    let sky = { top: '#7ec8f5', bottom: '#dff3ff' };
    let gravity = DEFAULT_GRAVITY;
    let score = null;
    let banner = null;
    let toast = null;
    let elapsed = 0;
    let camera = { x: 0, y: 0, follow: null, lead: 0.12, locked: false };
    let controls = [];
    let dragged = null;
    let dragOffset = { x: 0, y: 0 };

    // ---- geometry helpers

    function halfWidth(body) {
      return body.kind === 'circle' ? body.r : body.w / 2;
    }

    function halfHeight(body) {
      return body.kind === 'circle' ? body.r : body.h / 2;
    }

    function bottomOf(body) {
      return body.y + halfHeight(body);
    }

    function screenToWorld(px, py) {
      return { x: px + camera.x - canvas.clientWidth / 2, y: py + camera.y - canvas.clientHeight / 2 };
    }

    function hits(a, b) {
      if (a.kind === 'circle' && b.kind === 'circle') {
        return Math.hypot(a.x - b.x, a.y - b.y) < a.r + b.r;
      }
      // Everything non-circular collides as its bounding box, and a circle
      // against a box is close enough at these sizes that no child has ever
      // noticed the corners.
      return (
        Math.abs(a.x - b.x) < halfWidth(a) + halfWidth(b) && Math.abs(a.y - b.y) < halfHeight(a) + halfHeight(b)
      );
    }

    // ---- bodies

    let nextId = 1;

    function makeBody(spec) {
      const body = {
        id: nextId++,
        kind: spec.kind,
        x: spec.x || 0,
        y: spec.y || 0,
        vx: spec.vx || 0,
        vy: spec.vy || 0,
        r: spec.r || 20,
        w: spec.w || 40,
        h: spec.h || 40,
        color: spec.color || '#e53935',
        stroke: spec.stroke || null,
        text: spec.text || '',
        char: spec.char || '',
        size: spec.size || 40,
        tag: spec.tag || '',
        fixed: !!spec.fixed,
        bouncy: spec.bouncy == null ? 0.35 : spec.bouncy,
        friction: spec.friction == null ? 0.9 : spec.friction,
        gravityScale: spec.gravityScale == null ? 1 : spec.gravityScale,
        ghost: !!spec.ghost,
        angle: spec.angle || 0,
        spin: spec.spin || 0,
        rolls: spec.rolls !== false,
        alive: true,
        onGround: false,
        // Jump bookkeeping, so "longest jump" is a value the model reads rather
        // than a mechanism it has to invent and get subtly wrong.
        lastJump: { distance: 0, height: 0, airtime: 0 },
        airborne: null,
        landHandlers: [],
        removeHandlers: [],
      };

      body.push = (dx, dy) => {
        body.vx += dx || 0;
        body.vy += dy || 0;
        return body;
      };
      body.moveTo = (x, y) => {
        body.x = x;
        body.y = y;
        body.vx = 0;
        body.vy = 0;
        return body;
      };
      body.stop = () => {
        body.vx = 0;
        body.vy = 0;
        return body;
      };
      body.jump = (strength) => {
        if (!body.onGround) return body;
        body.vy = -(strength || 700);
        body.onGround = false;
        return body;
      };
      body.onLand = (handler) => {
        body.landHandlers.push(handler);
        return body;
      };
      body.onRemove = (handler) => {
        body.removeHandlers.push(handler);
        return body;
      };
      body.remove = () => {
        if (!body.alive) return body;
        body.alive = false;
        fire(body.removeHandlers, [body], onError);
        return body;
      };
      body.distanceTo = (other) => Math.hypot(body.x - other.x, body.y - other.y) / PIXELS_PER_METER;

      bodies.push(body);
      return body;
    }

    // ---- physics

    function stepBody(body, dt) {
      if (body.fixed || body === dragged) return;

      body.vy += gravity * body.gravityScale * dt;
      body.x += body.vx * dt;
      body.y += body.vy * dt;

      const wasAirborne = !body.onGround;
      body.onGround = false;

      if (!body.ghost) {
        const surface = terrain.surfaceAt(body.x);
        if (surface != null) {
          const foot = bottomOf(body);
          if (foot >= surface) {
            body.y -= foot - surface;
            const { tx, ty } = terrain.tangentAt(body.x);

            // Split velocity into "along the hill" and "into the hill". The
            // second half is what bounces; the first is what keeps rolling,
            // and it is where downhill acceleration comes from.
            const along = body.vx * tx + body.vy * ty;
            const into = body.vx * -ty + body.vy * tx;
            const kept = into < 0 ? -into * body.bouncy : 0;

            let nextAlong = along + gravity * body.gravityScale * ty * dt;
            nextAlong *= body.friction === 1 ? 1 : 1 - (1 - body.friction) * 0.35;

            body.vx = nextAlong * tx + kept * -ty;
            body.vy = nextAlong * ty + kept * tx;

            // Below this a ball on a flat surface jitters forever instead of resting.
            if (Math.abs(body.vy) < 40 && Math.abs(ty) < 0.05) body.vy = 0;
            body.onGround = true;
            if (body.rolls && body.kind === 'circle' && body.r > 0) body.spin = body.vx / body.r;
          }
        }
      }

      trackFlight(body, wasAirborne);

      body.angle += body.spin * dt;
    }

    function trackFlight(body, wasAirborne) {
      if (!body.onGround) {
        if (!body.airborne) {
          body.airborne = { x: body.x, y: body.y, peak: body.y, at: elapsed };
        } else if (body.y < body.airborne.peak) {
          body.airborne.peak = body.y;
        }
        return;
      }

      if (wasAirborne && body.airborne) {
        const flight = body.airborne;
        body.airborne = null;
        const airtime = elapsed - flight.at;
        // A step off a kerb is not a jump. Without this every game's "longest
        // jump" record is set in the first second by the ball settling.
        if (airtime < 0.25) return;
        body.lastJump = {
          distance: Math.abs(body.x - flight.x) / PIXELS_PER_METER,
          height: Math.max(0, flight.y - flight.peak) / PIXELS_PER_METER,
          airtime,
        };
        fire(body.landHandlers, [body.lastJump, body], onError);
      }
    }

    function resolvePair(a, b) {
      if (a.ghost || b.ghost || !a.alive || !b.alive) return;
      if (a.fixed && b.fixed) return;

      const dx = b.x - a.x;
      const dy = b.y - a.y;
      const overlapX = halfWidth(a) + halfWidth(b) - Math.abs(dx);
      const overlapY = halfHeight(a) + halfHeight(b) - Math.abs(dy);
      if (overlapX <= 0 || overlapY <= 0) return;

      // Push apart along whichever axis is least buried - the standard cheap
      // resolution, and the one that makes landing on a platform feel solid
      // instead of sliding off the side of it.
      const bounce = Math.max(a.bouncy, b.bouncy);
      if (overlapX < overlapY) {
        const push = (dx < 0 ? overlapX : -overlapX) / (a.fixed || b.fixed ? 1 : 2);
        if (!a.fixed) a.x += push;
        if (!b.fixed) b.x -= push;
        const relative = b.vx - a.vx;
        if (!a.fixed) a.vx = b.fixed ? -a.vx * bounce : a.vx + relative * 0.5;
        if (!b.fixed) b.vx = a.fixed ? -b.vx * bounce : b.vx - relative * 0.5;
      } else {
        const push = (dy < 0 ? overlapY : -overlapY) / (a.fixed || b.fixed ? 1 : 2);
        if (!a.fixed) a.y += push;
        if (!b.fixed) b.y -= push;
        const landedOnB = dy > 0 && b.fixed;
        const landedOnA = dy < 0 && a.fixed;
        if (landedOnB) {
          const wasAirborne = !a.onGround;
          a.vy = a.vy > 0 ? -a.vy * a.bouncy : a.vy;
          if (Math.abs(a.vy) < 60) a.vy = 0;
          a.onGround = true;
          a.vx *= a.friction;
          trackFlight(a, wasAirborne);
        } else if (landedOnA) {
          const wasAirborne = !b.onGround;
          b.vy = b.vy < 0 ? -b.vy * b.bouncy : b.vy;
          if (Math.abs(b.vy) < 60) b.vy = 0;
          b.onGround = true;
          b.vx *= b.friction;
          trackFlight(b, wasAirborne);
        } else {
          const relative = b.vy - a.vy;
          if (!a.fixed) a.vy += relative * 0.5;
          if (!b.fixed) b.vy -= relative * 0.5;
        }
      }
    }

    function matches(body, selector) {
      if (typeof selector === 'string') return body.tag === selector;
      return body === selector;
    }

    function runHitRules() {
      for (let r = 0; r < hitRules.length; r++) {
        const rule = hitRules[r];
        for (let i = 0; i < bodies.length; i++) {
          const a = bodies[i];
          if (!a.alive || !matches(a, rule.a)) continue;
          for (let j = 0; j < bodies.length; j++) {
            const b = bodies[j];
            if (a === b || !b.alive || !matches(b, rule.b)) continue;
            if (!hits(a, b)) continue;
            try {
              rule.handler(a, b);
            } catch (err) {
              onError(err);
            }
          }
        }
      }
    }

    // ---- controls

    function applyControls(dt) {
      input.applyTouchSteering();
      for (let i = 0; i < controls.length; i++) {
        const control = controls[i];
        const body = control.body;
        if (!body.alive) continue;

        const keys = input.keys;
        if (control.style === 'topdown') {
          const speed = control.speed;
          body.vx = (keys.right ? speed : 0) - (keys.left ? speed : 0);
          body.vy = (keys.down ? speed : 0) - (keys.up ? speed : 0);
          continue;
        }

        // 'platformer' - accelerate toward a target speed rather than snapping
        // to it, so a ball on a hill still behaves like a ball.
        const target = (keys.right ? control.speed : 0) - (keys.left ? control.speed : 0);
        if (target !== 0) {
          body.vx += clamp(target - body.vx, -control.accel * dt, control.accel * dt);
        } else if (body.onGround) {
          body.vx *= 0.86;
        }
        if ((keys.up || keys.space) && body.onGround && control.jump > 0) {
          body.vy = -control.jump;
          body.onGround = false;
          sound.play('jump');
        }
      }
    }

    function updateDrag() {
      const p = input.pointer;
      const world = screenToWorld(p.x, p.y);
      p.worldX = world.x;
      p.worldY = world.y;

      if (!p.down) {
        if (dragged) {
          dragged = null;
        }
        return;
      }

      if (!dragged) {
        for (let i = bodies.length - 1; i >= 0; i--) {
          const body = bodies[i];
          if (!body.alive || !body.draggable) continue;
          if (Math.abs(world.x - body.x) < halfWidth(body) + 12 && Math.abs(world.y - body.y) < halfHeight(body) + 12) {
            dragged = body;
            dragOffset = { x: body.x - world.x, y: body.y - world.y };
            break;
          }
        }
        return;
      }

      const nextX = world.x + dragOffset.x;
      const nextY = world.y + dragOffset.y;
      // Carry the drag speed into the throw, which is the entire reason a child
      // picks something up in the first place.
      dragged.vx = (nextX - dragged.x) * 12;
      dragged.vy = (nextY - dragged.y) * 12;
      dragged.x = nextX;
      dragged.y = nextY;
    }

    // ---- drawing

    function drawTerrain() {
      if (terrain.isEmpty) return;
      const points = terrain.points;
      const floor = camera.y + canvas.clientHeight;
      ctx.beginPath();
      ctx.moveTo(points[0][0], points[0][1]);
      for (let i = 1; i < points.length; i++) ctx.lineTo(points[i][0], points[i][1]);
      ctx.lineTo(points[points.length - 1][0], floor + 2000);
      ctx.lineTo(points[0][0], floor + 2000);
      ctx.closePath();
      ctx.fillStyle = terrain.style.soil;
      ctx.fill();

      ctx.beginPath();
      ctx.moveTo(points[0][0], points[0][1]);
      for (let i = 1; i < points.length; i++) ctx.lineTo(points[i][0], points[i][1]);
      ctx.lineWidth = 14;
      ctx.strokeStyle = terrain.style.color;
      ctx.lineJoin = 'round';
      ctx.stroke();
    }

    function drawBody(body) {
      ctx.save();
      ctx.translate(body.x, body.y);
      if (body.angle) ctx.rotate(body.angle);
      ctx.fillStyle = body.color;

      if (body.kind === 'circle') {
        ctx.beginPath();
        ctx.arc(0, 0, body.r, 0, Math.PI * 2);
        ctx.fill();
        if (body.stroke) {
          ctx.lineWidth = 3;
          ctx.strokeStyle = body.stroke;
          ctx.stroke();
        }
        // A plain filled circle has no way to show that it is rolling.
        if (body.rolls) {
          ctx.beginPath();
          ctx.arc(body.r * 0.4, 0, Math.max(2, body.r * 0.16), 0, Math.PI * 2);
          ctx.fillStyle = 'rgba(255,255,255,0.55)';
          ctx.fill();
        }
      } else if (body.kind === 'emoji') {
        ctx.font = `${body.size}px system-ui, "Apple Color Emoji", "Segoe UI Emoji", sans-serif`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(body.char, 0, 0);
      } else if (body.kind === 'text') {
        ctx.font = `bold ${body.size}px system-ui, sans-serif`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(body.text, 0, 0);
      } else {
        ctx.fillRect(-body.w / 2, -body.h / 2, body.w, body.h);
        if (body.stroke) {
          ctx.lineWidth = 3;
          ctx.strokeStyle = body.stroke;
          ctx.strokeRect(-body.w / 2, -body.h / 2, body.w, body.h);
        }
      }
      ctx.restore();
    }

    function drawHud() {
      const width = canvas.clientWidth;
      ctx.textAlign = 'left';
      ctx.textBaseline = 'top';

      if (score != null) {
        ctx.font = 'bold 28px system-ui, sans-serif';
        ctx.fillStyle = 'rgba(0,0,0,0.55)';
        ctx.fillText(String(score), 21, 17);
        ctx.fillStyle = '#fff';
        ctx.fillText(String(score), 20, 16);
      }

      const ids = Object.keys(records);
      if (ids.length) {
        ctx.font = '600 15px system-ui, sans-serif';
        ctx.textAlign = 'right';
        for (let i = 0; i < ids.length; i++) {
          const record = records[ids[i]];
          const text = `${record.label}: ${formatValue(record.value, record.unit)}`;
          ctx.fillStyle = 'rgba(0,0,0,0.45)';
          ctx.fillText(text, width - 19, 17 + i * 22);
          ctx.fillStyle = 'rgba(255,255,255,0.92)';
          ctx.fillText(text, width - 20, 16 + i * 22);
        }
      }

      if (banner && banner.until > elapsed) {
        ctx.textAlign = 'center';
        ctx.font = 'bold 34px system-ui, sans-serif';
        const y = canvas.clientHeight * 0.28;
        ctx.fillStyle = 'rgba(0,0,0,0.5)';
        ctx.fillText(banner.text, width / 2 + 2, y + 2);
        ctx.fillStyle = '#fff';
        ctx.fillText(banner.text, width / 2, y);
      }

      if (toast && toast.until > elapsed) {
        const remaining = toast.until - elapsed;
        const alpha = clamp(remaining, 0, 1);
        ctx.textAlign = 'center';
        ctx.font = 'bold 26px system-ui, sans-serif';
        ctx.fillStyle = `rgba(255, 214, 64, ${alpha})`;
        ctx.fillText(toast.text, width / 2, canvas.clientHeight * 0.14);
      }
    }

    function formatValue(value, unit) {
      const rounded = Math.abs(value) >= 100 ? Math.round(value) : Math.round(value * 10) / 10;
      return unit ? `${rounded}${unit}` : String(rounded);
    }

    function drawParticles() {
      for (let i = 0; i < particles.length; i++) {
        const p = particles[i];
        ctx.globalAlpha = clamp(p.life, 0, 1);
        ctx.fillStyle = p.color;
        ctx.fillRect(p.x - 4, p.y - 4, 8, 8);
      }
      ctx.globalAlpha = 1;
    }

    function draw() {
      const width = canvas.clientWidth;
      const height = canvas.clientHeight;

      const gradient = ctx.createLinearGradient(0, 0, 0, height);
      gradient.addColorStop(0, sky.top);
      gradient.addColorStop(1, sky.bottom);
      ctx.fillStyle = gradient;
      ctx.fillRect(0, 0, width, height);

      ctx.save();
      ctx.translate(width / 2 - camera.x, height / 2 - camera.y);
      drawTerrain();
      for (let i = 0; i < bodies.length; i++) if (bodies[i].alive) drawBody(bodies[i]);
      drawParticles();
      ctx.restore();

      drawHud();
    }

    // ---- the world object handed to game code

    const world = {
      /** Seconds since the game started. */
      get time() {
        return elapsed;
      },
      get width() {
        return canvas.clientWidth;
      },
      get height() {
        return canvas.clientHeight;
      },
      get bodies() {
        return bodies.filter((b) => b.alive);
      },
      keys: input.keys,
      pointer: input.pointer,

      // -- scene
      sky(top, bottom) {
        sky = { top: top || '#7ec8f5', bottom: bottom || top || '#dff3ff' };
        return world;
      },
      gravity(value) {
        gravity = value == null ? DEFAULT_GRAVITY : value;
        return world;
      },
      groundStyle(grass, soil) {
        terrain.setStyle({ color: grass || '#4caf50', soil: soil || '#6d4c41' });
        return world;
      },
      ground(y, from, to) {
        const left = from == null ? -2000 : from;
        const right = to == null ? 4000 : to;
        terrain.add([
          [left, y],
          [right, y],
        ]);
        return world;
      },
      hill(fromX, fromY, toX, toY, steps) {
        const count = Math.max(4, steps || 24);
        const points = [];
        for (let i = 0; i <= count; i++) {
          const t = i / count;
          // Cosine ease, so a hill has a crest and a foot rather than a ramp.
          const eased = (1 - Math.cos(t * Math.PI)) / 2;
          points.push([lerp(fromX, toX, t), lerp(fromY, toY, eased)]);
        }
        terrain.add(points);
        return world;
      },
      terrain(points) {
        terrain.add(points || []);
        return world;
      },
      clearGround() {
        terrain.clear();
        return world;
      },
      groundAt(x) {
        return terrain.surfaceAt(x);
      },

      // -- bodies
      ball(spec) {
        return makeBody(Object.assign({ kind: 'circle' }, spec));
      },
      box(spec) {
        return makeBody(Object.assign({ kind: 'box', rolls: false }, spec));
      },
      emoji(spec) {
        const size = (spec && spec.size) || 48;
        return makeBody(Object.assign({ kind: 'emoji', rolls: false, w: size, h: size }, spec, { size }));
      },
      label(spec) {
        const size = (spec && spec.size) || 28;
        return makeBody(
          Object.assign({ kind: 'text', rolls: false, fixed: true, ghost: true, color: '#ffffff' }, spec, { size }),
        );
      },
      remove(body) {
        if (body && body.remove) body.remove();
        return world;
      },

      // -- control and camera
      control(body, spec) {
        const settings = spec || {};
        controls = controls.filter((c) => c.body !== body);
        controls.push({
          body,
          style: settings.style === 'topdown' ? 'topdown' : 'platformer',
          speed: settings.speed == null ? 320 : settings.speed,
          jump: settings.jump == null ? 720 : settings.jump,
          accel: settings.accel == null ? 2600 : settings.accel,
        });
        // Drag comes along by default: the same child who cannot reach the
        // keys can always grab the ball.
        body.draggable = settings.draggable !== false;
        return body;
      },
      draggable(body, on) {
        body.draggable = on !== false;
        return body;
      },
      follow(body, lead) {
        camera.follow = body;
        camera.lead = lead == null ? 0.12 : lead;
        camera.locked = false;
        return world;
      },
      lookAt(x, y) {
        camera.follow = null;
        camera.locked = true;
        camera.x = x;
        camera.y = y;
        return world;
      },

      // -- rules
      onKey(name, handler) {
        input.onKey(name, handler);
        return world;
      },
      onTap(handler) {
        input.onTap(handler);
        return world;
      },
      onHit(a, b, handler) {
        hitRules.push({ a, b, handler });
        return world;
      },
      every(seconds, handler) {
        const timer = { every: Math.max(0.016, seconds), next: elapsed + seconds, handler, alive: true };
        timers.push(timer);
        return () => {
          timer.alive = false;
        };
      },
      after(seconds, handler) {
        const timer = { every: null, next: elapsed + seconds, handler, alive: true };
        timers.push(timer);
        return () => {
          timer.alive = false;
        };
      },

      // -- feedback
      score(value) {
        score = value;
        return world;
      },
      addScore(delta) {
        score = (score || 0) + (delta == null ? 1 : delta);
        return score;
      },
      say(text, seconds) {
        banner = { text: String(text), until: elapsed + (seconds == null ? 2 : seconds) };
        return world;
      },
      sound(name) {
        sound.play(name);
        return world;
      },
      confetti(x, y, color) {
        for (let i = 0; i < 26; i++) {
          particles.push({
            x,
            y,
            vx: (Math.random() - 0.5) * 420,
            vy: -Math.random() * 460 - 60,
            life: 1,
            color: color || ['#ffd54f', '#4fc3f7', '#ff8a65', '#aed581'][i % 4],
          });
        }
        return world;
      },

      /**
       * The gamification hook. Report a value whenever one happens; the engine
       * keeps the best it has seen, announces it when it is beaten, and hands
       * it to the host to persist so a record set on Tuesday is still standing
       * on Saturday.
       */
      record(id, label, value, spec) {
        if (!Number.isFinite(value)) return false;
        const settings = spec || {};
        const lowerWins = settings.lowerIsBetter === true;
        const existing = records[id];
        const beaten = !existing || (lowerWins ? value < existing.value : value > existing.value);
        if (!beaten) return false;

        records[id] = { id, label, value, unit: settings.unit || '', lowerIsBetter: lowerWins };
        // Nothing to celebrate about the very first value of a brand new
        // record - it is not a personal best, it is just the first one.
        if (existing) {
          toast = { text: `${label}! ${formatValue(value, settings.unit || '')}`, until: elapsed + 2.6 };
          sound.play('win');
        }
        onRecord(records);
        return true;
      },
      bestRecord(id) {
        return records[id] ? records[id].value : null;
      },
      meters(pixels) {
        return pixels / PIXELS_PER_METER;
      },
      pixels(meters) {
        return meters * PIXELS_PER_METER;
      },
      random(low, high) {
        return low + Math.random() * (high - low);
      },
      pick(list) {
        return list[Math.floor(Math.random() * list.length)];
      },
    };

    // ---- the loop

    function step(dt) {
      elapsed += dt;

      applyControls(dt);
      updateDrag();

      for (let i = 0; i < bodies.length; i++) if (bodies[i].alive) stepBody(bodies[i], dt);

      for (let i = 0; i < bodies.length; i++) {
        if (!bodies[i].alive) continue;
        for (let j = i + 1; j < bodies.length; j++) {
          if (!bodies[j].alive) continue;
          resolvePair(bodies[i], bodies[j]);
        }
      }

      runHitRules();

      for (let i = timers.length - 1; i >= 0; i--) {
        const timer = timers[i];
        if (!timer.alive) {
          timers.splice(i, 1);
          continue;
        }
        if (elapsed < timer.next) continue;
        if (timer.every == null) {
          timer.alive = false;
          timers.splice(i, 1);
        } else {
          timer.next = elapsed + timer.every;
        }
        try {
          timer.handler();
        } catch (err) {
          onError(err);
        }
      }

      for (let i = particles.length - 1; i >= 0; i--) {
        const p = particles[i];
        p.vy += gravity * 0.5 * dt;
        p.x += p.vx * dt;
        p.y += p.vy * dt;
        p.life -= dt * 0.8;
        if (p.life <= 0) particles.splice(i, 1);
      }

      // Dead bodies are swept after the step so a handler can still read the
      // body that just triggered it.
      for (let i = bodies.length - 1; i >= 0; i--) if (!bodies[i].alive) bodies.splice(i, 1);

      if (camera.follow && camera.follow.alive) {
        camera.x = lerp(camera.x, camera.follow.x, 1 - Math.pow(camera.lead, dt * 60));
        camera.y = lerp(camera.y, camera.follow.y - canvas.clientHeight * 0.12, 1 - Math.pow(camera.lead, dt * 60));
      }
    }

    return {
      world,
      step(dt) {
        step(Math.min(dt, MAX_STEP));
      },
      draw,
      /** Called by the host when a fresh version of the game replaces this one. */
      dispose() {
        bodies.length = 0;
        timers.length = 0;
        hitRules.length = 0;
        particles.length = 0;
        controls = [];
      },
      records() {
        return records;
      },
    };
  }

  global.AerieEngine = { createWorld, PIXELS_PER_METER };
})(window);

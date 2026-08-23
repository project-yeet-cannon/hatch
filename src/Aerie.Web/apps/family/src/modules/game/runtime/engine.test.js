import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { runGame } from './harness';

/*
  What the engine promises the model.

  These are behavioural rather than numeric wherever possible - "the ball ends
  up resting on the ground", not "y is 494.3". The exact numbers are free to
  change when the physics is tuned; what must not change is that a ball put
  above a hill rolls down it, because that sentence is in the system prompt.
*/

const game = (setup, update) =>
  `defineGame({ setup(w) { ${setup} }, update(w, dt) { ${update ?? ''} } });`;

describe('gravity and ground', () => {
  it('drops a ball onto flat ground and lets it settle', () => {
    const { world, run } = runGame(game(`
      w.ground(500, -1000, 1000);
      w.ball({ x: 0, y: 100, r: 20, bouncy: 0 });
    `));

    run(3);

    const [ball] = world.bodies;
    // Resting on the surface: the ball's foot is the ground, not its centre.
    expect(ball.y + ball.r).toBeCloseTo(500, 0);
    expect(ball.onGround).toBe(true);
    expect(Math.abs(ball.vy)).toBeLessThan(1);
  });

  it('rolls a ball down a hill', () => {
    const { world, run } = runGame(game(`
      w.hill(0, 200, 900, 520);
      w.ball({ x: 40, y: 150, r: 20 });
    `));

    run(2.5);

    const [ball] = world.bodies;
    // The whole of "i want to roll down a hill" is this assertion.
    expect(ball.vx).toBeGreaterThan(50);
    expect(ball.x).toBeGreaterThan(200);
  });

  it('leaves a ball alone where there is no ground at all', () => {
    const { world, run } = runGame(game(`
      w.ground(500, 0, 100);
      w.ball({ x: 400, y: 0, r: 20 });
    `));

    run(1);

    // Past the end of the profile there is nothing to stand on, and falling is
    // the honest answer - not snapping to the last surface that existed.
    expect(world.bodies[0].y).toBeGreaterThan(500);
    expect(world.bodies[0].onGround).toBe(false);
  });
});

describe('jumps', () => {
  it('measures a jump on landing and tells the body about it', () => {
    let landed = null;
    const { world, run } = runGame(game(`
      w.ground(500, -1000, 2000);
      const ball = w.ball({ x: 0, y: 460, r: 20, bouncy: 0 });
      ball.onLand((jump) => { w.say('landed'); ball.lastLanding = jump; });
      w.after(0.5, () => { ball.vy = -700; ball.vx = 300; });
    `));

    run(3);
    landed = world.bodies[0].lastLanding;

    expect(landed).not.toBeNull();
    expect(landed.distance).toBeGreaterThan(1);
    expect(landed.height).toBeGreaterThan(0.5);
    expect(landed.airtime).toBeGreaterThan(0.25);
  });

  it('does not count settling onto the ground as a jump', () => {
    const { world, run } = runGame(game(`
      w.ground(500, -1000, 1000);
      const ball = w.ball({ x: 0, y: 470, r: 20, bouncy: 0.4 });
      ball.onLand(() => { ball.landings = (ball.landings || 0) + 1; });
    `));

    run(3);

    // Without the airtime floor, every game's longest-jump record is set in the
    // first second by a ball bouncing to a stop.
    expect(world.bodies[0].landings).toBeUndefined();
  });
});

describe('records', () => {
  it('keeps the best, and only the best', () => {
    const { world } = runGame(game(`w.ground(500, -100, 100);`));

    expect(world.record('jump', 'Longest jump', 4, { unit: 'm' })).toBe(true);
    expect(world.record('jump', 'Longest jump', 2, { unit: 'm' })).toBe(false);
    expect(world.record('jump', 'Longest jump', 9, { unit: 'm' })).toBe(true);
    expect(world.bestRecord('jump')).toBe(9);
  });

  it('honours lowerIsBetter', () => {
    const { world } = runGame(game(`w.ground(500, -100, 100);`));

    world.record('lap', 'Fastest lap', 30, { unit: 's', lowerIsBetter: true });
    expect(world.record('lap', 'Fastest lap', 40, { unit: 's', lowerIsBetter: true })).toBe(false);
    expect(world.record('lap', 'Fastest lap', 20, { unit: 's', lowerIsBetter: true })).toBe(true);
    expect(world.bestRecord('lap')).toBe(20);
  });

  it('starts from the records it was seeded with, and reports every change', () => {
    const changes = [];
    const { world } = runGame(game(`w.ground(500, -100, 100);`), {
      records: { jump: { id: 'jump', label: 'Longest jump', value: 12, unit: 'm' } },
      onRecord: (records) => changes.push(records.jump.value),
    });

    // Seeding is what stops a fresh version announcing the first jump of the
    // day as a new best.
    expect(world.bestRecord('jump')).toBe(12);
    expect(world.record('jump', 'Longest jump', 8, { unit: 'm' })).toBe(false);
    expect(world.record('jump', 'Longest jump', 15, { unit: 'm' })).toBe(true);
    expect(changes).toEqual([15]);
  });

  it('ignores a value that is not a number', () => {
    const { world } = runGame(game(`w.ground(500, -100, 100);`));

    // Reporting `undefined` is what a game does on its first frame, before the
    // thing being measured has happened.
    expect(world.record('jump', 'Longest jump', undefined)).toBe(false);
    expect(world.record('jump', 'Longest jump', NaN)).toBe(false);
    expect(world.bestRecord('jump')).toBeNull();
  });
});

describe('rules', () => {
  it('runs a hit rule by tag and stops once the body is gone', () => {
    const { world, run } = runGame(game(`
      w.gravity(0);
      const ball = w.ball({ x: 0, y: 0, r: 20, tag: 'ball' });
      w.emoji({ x: 20, y: 0, char: 'x', size: 20, tag: 'star', ghost: true });
      w.score(0);
      w.onHit(ball, 'star', (_b, star) => { star.remove(); w.addScore(1); });
    `));

    run(0.2);

    expect(world.bodies).toHaveLength(1);
    // The rule keeps matching every frame while they overlap; removing the star
    // is what has to make it stop.
    expect(world.bodies[0].tag).toBe('ball');
  });

  it('repeats an interval and fires a delay once', () => {
    const { world, run } = runGame(game(`
      w.gravity(0);
      w.ticks = 0;
      w.onces = 0;
      w.every(0.1, () => { w.ticks++; });
      w.after(0.1, () => { w.onces++; });
    `));

    run(1);

    expect(world.ticks).toBeGreaterThan(5);
    expect(world.onces).toBe(1);
  });

  it('cancels a timer when asked', () => {
    const { world, run } = runGame(game(`
      w.gravity(0);
      w.ticks = 0;
      const stop = w.every(0.1, () => { w.ticks++; });
      w.after(0.25, stop);
    `));

    run(1);

    expect(world.ticks).toBeLessThan(4);
  });

  it('keeps playing when a rule throws', () => {
    const errors = [];
    const { world, run } = runGame(
      game(`
        w.ground(500, -1000, 1000);
        w.ball({ x: 0, y: 100, r: 20 });
        w.every(0.1, () => { throw new Error('bad rule'); });
      `),
      { onError: (err) => errors.push(err) },
    );

    run(0.5);

    // One bad rule is not the end of the game: it is reported and the ball
    // keeps falling.
    expect(errors.length).toBeGreaterThan(0);
    expect(world.bodies[0].y).toBeGreaterThan(100);
  });
});

describe('control', () => {
  it('moves the controlled body when a key is held', () => {
    const { world, run } = runGame(game(`
      w.ground(500, -1000, 2000);
      const ball = w.ball({ x: 0, y: 470, r: 20 });
      w.control(ball, { speed: 300 });
    `));

    world.keys.right = true;
    run(1);

    expect(world.bodies[0].x).toBeGreaterThan(50);
  });

  it('jumps only from the ground', () => {
    const { world, run } = runGame(game(`
      w.ground(500, -1000, 2000);
      const ball = w.ball({ x: 0, y: 470, r: 20, bouncy: 0 });
      w.control(ball, { jump: 700 });
    `));

    run(1);
    const resting = world.bodies[0].y;

    world.keys.space = true;
    run(0.2);
    const rising = world.bodies[0].y;
    world.keys.space = false;

    expect(rising).toBeLessThan(resting);

    // Mid-air, still holding jump, velocity may only be bent by gravity.
    // Leaning on the space bar is the first thing anyone tries, and a second
    // impulse would show up here as vy snapping back to -700.
    world.keys.space = true;
    const rate = world.bodies[0].vy;
    run(0.05);
    expect(world.bodies[0].onGround).toBe(false);
    expect(world.bodies[0].vy).toBeGreaterThan(rate);
  });
});

describe('what the model is told', () => {
  /**
   * The worked example in the system prompt and the code every new world starts
   * on are both read out of the C# they live in, rather than copied here. A
   * copy would pass forever while the originals rotted, which is the exact
   * failure this is meant to catch: the example is what the model imitates, so
   * an example that does not run is a bad turn every time.
   */
  const csharp = (file) => readFileSync(findUp(join('src/Aerie.Api/Modules/Game', file)), 'utf8');

  it('runs the example from the system prompt', () => {
    const source = csharp('GameEngineReference.cs');
    // The last one: the first <code> in the file is the response-format
    // template, whose body is a description rather than code.
    const blocks = [...source.matchAll(/<code>\n([\s\S]*?)\n\s*<\/code>/g)];
    expect(blocks.length).toBeGreaterThan(0);

    const { world, run } = runGame(blocks[blocks.length - 1][1]);
    run(4);

    expect(world.bodies.length).toBeGreaterThan(0);
    expect(world.time).toBeGreaterThan(3);
  });

  it('runs the code a brand new world starts on', () => {
    const source = csharp('GameService.cs');
    const seed = source.match(/public const string SeedCode = """\n([\s\S]*?)\n\s*""";/);
    expect(seed).not.toBeNull();

    const { world, run } = runGame(seed[1]);
    run(4);

    expect(world.bodies).toHaveLength(2);
  });
});

/** Walks up from this file to the repo, so the path here survives a move of either side. */
function findUp(relative) {
  let directory = dirname(fileURLToPath(import.meta.url));
  for (;;) {
    const candidate = join(directory, relative);
    if (existsSync(candidate)) return candidate;
    const parent = dirname(directory);
    if (parent === directory) throw new Error(`Could not find ${relative} above this file.`);
    directory = parent;
  }
}

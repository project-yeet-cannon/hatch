import { describe, expect, it } from 'vitest';
import { distance } from './geometry';
import { solveSketch } from './solver';
import type { WallSegment } from './schema';

function wall(id: string, start: { x: number; y: number }, end: { x: number; y: number }, measuredLength?: number): WallSegment {
  return { id, start, end, thickness: 0.15, ...(measuredLength !== undefined ? { measuredLength } : {}) };
}

describe('solveSketch', () => {
  it('returns nothing for an empty sketch', () => {
    const result = solveSketch([]);
    expect(result.walls).toEqual([]);
    expect(result.statuses.size).toBe(0);
  });

  it('marks a measured wall as measured and pulls it to the target length', () => {
    const walls = [wall('w1', { x: 0, y: 0 }, { x: 2.9, y: 0.02 }, 3)];
    const result = solveSketch(walls);
    expect(result.statuses.get('w1')).toBe('measured');
    expect(result.lengths.get('w1')).toBeCloseTo(3, 2);
  });

  it('marks an unmeasured, unconnected wall as estimated and leaves its length near the sketch guess', () => {
    const walls = [wall('w1', { x: 0, y: 0 }, { x: 2, y: 2 })]; // ~45°, not axis-aligned, not near any other wall
    const result = solveSketch(walls);
    expect(result.statuses.get('w1')).toBe('estimated');
    expect(result.lengths.get('w1')).toBeCloseTo(distance({ x: 0, y: 0 }, { x: 2, y: 2 }), 1);
  });

  it('snaps a near-horizontal, near-vertical wall pair to an exact right angle', () => {
    const walls = [
      wall('bottom', { x: 0, y: 0.03 }, { x: 4, y: -0.02 }),
      wall('side', { x: 4, y: -0.02 }, { x: 4.02, y: 3 }),
    ];
    const result = solveSketch(walls);
    const bottom = result.walls.find((w) => w.id === 'bottom')!;
    const side = result.walls.find((w) => w.id === 'side')!;
    expect(bottom.start.y).toBeCloseTo(bottom.end.y, 3);
    expect(side.start.x).toBeCloseTo(side.end.x, 3);
  });

  it('derives the opposite two sides of an axis-aligned rectangle from two adjacent measurements', () => {
    // A 4m x 3m rectangle, sketched slightly off-true, with only the two walls meeting at the
    // origin corner measured. The other two corners/lengths should come out "derived" and correct
    // because the axis locks force the rectangle closed once two adjacent sides are pinned.
    const a = { x: 0, y: 0 };
    const b = { x: 4.1, y: 0.05 };
    const c = { x: 4.05, y: 2.9 };
    const d = { x: -0.05, y: 3.1 };
    const walls = [
      wall('ab', a, b, 4),
      wall('bc', b, c),
      wall('cd', c, d),
      wall('da', d, a, 3),
    ];

    const result = solveSketch(walls);

    expect(result.statuses.get('ab')).toBe('measured');
    expect(result.statuses.get('da')).toBe('measured');
    expect(result.statuses.get('bc')).toBe('derived');
    expect(result.statuses.get('cd')).toBe('derived');
    expect(result.lengths.get('bc')).toBeCloseTo(3, 1);
    expect(result.lengths.get('cd')).toBeCloseTo(4, 1);
  });

  it('keeps solved wall ids and thickness unchanged', () => {
    const walls = [wall('w1', { x: 0, y: 0 }, { x: 3, y: 0 }, 3)];
    const result = solveSketch(walls);
    expect(result.walls[0].id).toBe('w1');
    expect(result.walls[0].thickness).toBe(0.15);
  });
});

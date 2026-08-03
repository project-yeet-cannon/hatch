import { describe, expect, it } from 'vitest';
import {
  distance,
  nearestPoint,
  nearestPointOnSegment,
  snapDrawPoint,
  snapToAngle,
  snapToGrid,
} from './geometry';

describe('snapToGrid', () => {
  it('rounds to the nearest grid step', () => {
    expect(snapToGrid({ x: 1.04, y: 1.06 }, 0.1)).toEqual({ x: 1, y: 1.1 });
  });
});

describe('nearestPoint', () => {
  it('returns the closest candidate within tolerance', () => {
    const candidates = [{ x: 0, y: 0 }, { x: 5, y: 5 }];
    expect(nearestPoint({ x: 0.1, y: 0.1 }, candidates, 0.5)).toEqual({ x: 0, y: 0 });
  });

  it('returns null when nothing is within tolerance', () => {
    expect(nearestPoint({ x: 10, y: 10 }, [{ x: 0, y: 0 }], 0.5)).toBeNull();
  });

  it('returns null for an empty candidate list', () => {
    expect(nearestPoint({ x: 0, y: 0 }, [], 1)).toBeNull();
  });
});

describe('snapToAngle', () => {
  it('snaps a near-horizontal point onto the axis', () => {
    const origin = { x: 0, y: 0 };
    const snapped = snapToAngle(origin, { x: 3, y: 0.05 });
    expect(snapped.y).toBeCloseTo(0, 5);
    expect(distance(origin, snapped)).toBeCloseTo(distance(origin, { x: 3, y: 0.05 }), 5);
  });

  it('leaves the point untouched when no increment is within tolerance', () => {
    const origin = { x: 0, y: 0 };
    const point = { x: 3, y: 1.2 }; // ~22 degrees, well off the 15-degree grid
    expect(snapToAngle(origin, point)).toBe(point);
  });

  it('is a no-op at zero distance from origin', () => {
    const origin = { x: 1, y: 1 };
    expect(snapToAngle(origin, origin)).toBe(origin);
  });
});

describe('snapDrawPoint', () => {
  const origin = { x: 0, y: 0 };

  it('prefers snapping to an existing vertex over angle/grid', () => {
    const vertices = [{ x: 3, y: 0.02 }];
    const result = snapDrawPoint({ x: 2.98, y: 0.05 }, origin, vertices, 0.2);
    expect(result).toEqual({ point: vertices[0], kind: 'vertex' });
  });

  it('falls back to angle snapping near an axis', () => {
    const result = snapDrawPoint({ x: 3, y: 0.05 }, origin, [], 0.05);
    expect(result.kind).toBe('angle');
    expect(result.point.y).toBeCloseTo(0, 5);
  });

  it('falls back to grid snapping with no origin and no nearby vertex', () => {
    const result = snapDrawPoint({ x: 1.24, y: 2.37 }, null, [], 0.05);
    expect(result.kind).toBe('grid');
    expect(result.point.x).toBeCloseTo(1.2, 5);
    expect(result.point.y).toBeCloseTo(2.4, 5);
  });
});

describe('nearestPointOnSegment', () => {
  it('clamps to the segment start/end for points beyond either end', () => {
    const start = { x: 0, y: 0 };
    const end = { x: 4, y: 0 };
    expect(nearestPointOnSegment({ x: -2, y: 1 }, start, end)).toMatchObject({ point: start, t: 0 });
    expect(nearestPointOnSegment({ x: 10, y: 1 }, start, end)).toMatchObject({ point: end, t: 1 });
  });

  it('finds the perpendicular projection along the middle of the segment', () => {
    const result = nearestPointOnSegment({ x: 2, y: 3 }, { x: 0, y: 0 }, { x: 4, y: 0 });
    expect(result.point).toEqual({ x: 2, y: 0 });
    expect(result.t).toBeCloseTo(0.5, 5);
    expect(result.distance).toBeCloseTo(3, 5);
  });

  it('handles a zero-length segment without dividing by zero', () => {
    const result = nearestPointOnSegment({ x: 1, y: 1 }, { x: 5, y: 5 }, { x: 5, y: 5 });
    expect(result.point).toEqual({ x: 5, y: 5 });
    expect(Number.isFinite(result.distance)).toBe(true);
  });
});

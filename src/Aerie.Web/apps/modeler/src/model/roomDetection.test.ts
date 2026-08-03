import { describe, expect, it } from 'vitest';
import { createId } from './schema';
import type { Point2, WallSegment } from './schema';
import { detectRooms, matchRoomLabel, pointInPolygon } from './roomDetection';

function wall(start: Point2, end: Point2, thickness = 0.15): WallSegment {
  return { id: createId(), start, end, thickness };
}

function rectangleWalls(x0: number, y0: number, x1: number, y1: number): WallSegment[] {
  const a = { x: x0, y: y0 };
  const b = { x: x1, y: y0 };
  const c = { x: x1, y: y1 };
  const d = { x: x0, y: y1 };
  return [wall(a, b), wall(b, c), wall(c, d), wall(d, a)];
}

describe('detectRooms', () => {
  it('finds no rooms with no walls', () => {
    expect(detectRooms([])).toEqual([]);
  });

  it('detects a single closed rectangle as one room', () => {
    const rooms = detectRooms(rectangleWalls(0, 0, 4, 3));
    expect(rooms).toHaveLength(1);
    expect(rooms[0].area).toBeCloseTo(12, 5);
    expect(rooms[0].centroid).toEqual({ x: 2, y: 1.5 });
  });

  it('finds no rooms when the loop is open', () => {
    const walls = rectangleWalls(0, 0, 4, 3).slice(0, 3); // drop the last side
    expect(detectRooms(walls)).toEqual([]);
  });

  it('splits a rectangle into two rooms along a shared interior wall', () => {
    const walls: WallSegment[] = [
      wall({ x: 0, y: 0 }, { x: 2, y: 0 }),
      wall({ x: 2, y: 0 }, { x: 4, y: 0 }),
      wall({ x: 4, y: 0 }, { x: 4, y: 3 }),
      wall({ x: 4, y: 3 }, { x: 2, y: 3 }),
      wall({ x: 2, y: 3 }, { x: 0, y: 3 }),
      wall({ x: 0, y: 3 }, { x: 0, y: 0 }),
      wall({ x: 2, y: 0 }, { x: 2, y: 3 }), // interior partition
    ];

    const rooms = detectRooms(walls);
    expect(rooms).toHaveLength(2);
    const areas = rooms.map((r) => r.area).sort((a, b) => a - b);
    expect(areas[0]).toBeCloseTo(6, 5);
    expect(areas[1]).toBeCloseTo(6, 5);
  });

  it('detects two disjoint rectangles as two separate rooms', () => {
    const walls = [...rectangleWalls(0, 0, 2, 2), ...rectangleWalls(10, 10, 13, 12)];
    const rooms = detectRooms(walls);
    const areas = rooms.map((r) => Math.round(r.area * 100) / 100).sort((a, b) => a - b);
    expect(areas).toEqual([4, 6]);
  });

  it('ignores a dangling wall stub attached to a closed room', () => {
    const walls = [...rectangleWalls(0, 0, 4, 3), wall({ x: 4, y: 0 }, { x: 5, y: 0 })];
    const rooms = detectRooms(walls);
    expect(rooms).toHaveLength(1);
    expect(rooms[0].area).toBeCloseTo(12, 5);
  });

  it('does not double-count a duplicated wall segment', () => {
    const base = rectangleWalls(0, 0, 4, 3);
    const walls = [...base, wall(base[0].start, base[0].end)];
    expect(detectRooms(walls)).toHaveLength(1);
  });

  it('ignores zero-length walls', () => {
    const walls = [...rectangleWalls(0, 0, 4, 3), wall({ x: 1, y: 1 }, { x: 1, y: 1 })];
    expect(detectRooms(walls)).toHaveLength(1);
  });
});

describe('pointInPolygon', () => {
  const square = [{ x: 0, y: 0 }, { x: 4, y: 0 }, { x: 4, y: 4 }, { x: 0, y: 4 }];

  it('is true for a point inside', () => {
    expect(pointInPolygon({ x: 2, y: 2 }, square)).toBe(true);
  });

  it('is false for a point outside', () => {
    expect(pointInPolygon({ x: 5, y: 5 }, square)).toBe(false);
  });
});

describe('matchRoomLabel', () => {
  it('finds the label whose seed falls inside the room polygon', () => {
    const room = detectRooms(rectangleWalls(0, 0, 4, 3))[0];
    const labels = [
      { id: 'a', name: 'Elsewhere', seed: { x: 100, y: 100 } },
      { id: 'b', name: 'Kitchen', seed: { x: 2, y: 1.5 } },
    ];
    expect(matchRoomLabel(labels, room)?.name).toBe('Kitchen');
  });

  it('returns undefined when no label seed is inside', () => {
    const room = detectRooms(rectangleWalls(0, 0, 4, 3))[0];
    expect(matchRoomLabel([{ id: 'a', name: 'Elsewhere', seed: { x: 100, y: 100 } }], room)).toBeUndefined();
  });
});

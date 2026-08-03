import { describe, expect, it } from 'vitest';
import { defaultCamera, fitCamera, panBy, screenToWorld, worldToScreen, zoomAt } from './camera';

describe('worldToScreen / screenToWorld', () => {
  it('round-trips through the transform', () => {
    const camera = { pan: { x: 100, y: 50 }, zoom: 40 };
    const world = { x: 3.5, y: -2 };
    expect(screenToWorld(camera, worldToScreen(camera, world))).toEqual(world);
  });

  it('maps world origin to the pan offset', () => {
    const camera = { pan: { x: 100, y: 50 }, zoom: 40 };
    expect(worldToScreen(camera, { x: 0, y: 0 })).toEqual({ x: 100, y: 50 });
  });
});

describe('panBy', () => {
  it('shifts the pan offset without changing zoom', () => {
    const camera = defaultCamera();
    const next = panBy(camera, { x: 10, y: -5 });
    expect(next.pan).toEqual({ x: 10, y: -5 });
    expect(next.zoom).toBe(camera.zoom);
  });
});

describe('zoomAt', () => {
  it('keeps the world point under the pivot fixed on screen', () => {
    const camera = { pan: { x: 0, y: 0 }, zoom: 40 };
    const pivot = { x: 200, y: 150 };
    const worldUnderPivotBefore = screenToWorld(camera, pivot);

    const next = zoomAt(camera, pivot, 2);
    const worldUnderPivotAfter = screenToWorld(next, pivot);

    expect(worldUnderPivotAfter.x).toBeCloseTo(worldUnderPivotBefore.x, 6);
    expect(worldUnderPivotAfter.y).toBeCloseTo(worldUnderPivotBefore.y, 6);
    expect(next.zoom).toBeCloseTo(80, 6);
  });

  it('clamps to the min/max zoom range', () => {
    const camera = { pan: { x: 0, y: 0 }, zoom: 40 };
    expect(zoomAt(camera, { x: 0, y: 0 }, 0.0001).zoom).toBeGreaterThanOrEqual(5);
    expect(zoomAt(camera, { x: 0, y: 0 }, 100000).zoom).toBeLessThanOrEqual(800);
  });
});

describe('fitCamera', () => {
  it('centers the given world point in the viewport', () => {
    const camera = fitCamera({ x: 5, y: 5 }, { width: 10, height: 10 }, { width: 800, height: 600 });
    const screenCenter = worldToScreen(camera, { x: 5, y: 5 });
    expect(screenCenter.x).toBeCloseTo(400, 5);
    expect(screenCenter.y).toBeCloseTo(300, 5);
  });

  it('falls back to a sane zoom for a degenerate (zero-size) world box', () => {
    const camera = fitCamera({ x: 0, y: 0 }, { width: 0, height: 0 }, { width: 800, height: 600 });
    expect(camera.zoom).toBeGreaterThan(0);
    expect(Number.isFinite(camera.zoom)).toBe(true);
  });
});

import type { Point2 } from '../model/schema';

// Screen-space pixels per world meter.
export const DEFAULT_ZOOM = 60;
export const MIN_ZOOM = 5;
export const MAX_ZOOM = 800;

export interface Camera {
  /** Screen-space pixel offset of world point (0, 0). */
  pan: Point2;
  /** Screen pixels per world meter. */
  zoom: number;
}

export function defaultCamera(): Camera {
  return { pan: { x: 0, y: 0 }, zoom: DEFAULT_ZOOM };
}

export function worldToScreen(camera: Camera, world: Point2): Point2 {
  return { x: world.x * camera.zoom + camera.pan.x, y: world.y * camera.zoom + camera.pan.y };
}

export function screenToWorld(camera: Camera, screen: Point2): Point2 {
  return { x: (screen.x - camera.pan.x) / camera.zoom, y: (screen.y - camera.pan.y) / camera.zoom };
}

export function panBy(camera: Camera, deltaScreen: Point2): Camera {
  return { ...camera, pan: { x: camera.pan.x + deltaScreen.x, y: camera.pan.y + deltaScreen.y } };
}

/** Zooms by `factor` while keeping the world point currently under `pivotScreen` fixed on screen. */
export function zoomAt(camera: Camera, pivotScreen: Point2, factor: number): Camera {
  const worldBefore = screenToWorld(camera, pivotScreen);
  const zoom = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, camera.zoom * factor));
  return {
    zoom,
    pan: { x: pivotScreen.x - worldBefore.x * zoom, y: pivotScreen.y - worldBefore.y * zoom },
  };
}

/** A camera centered on `worldCenter` at the zoom that fits `worldSize` inside `viewportSize`, with margin. */
export function fitCamera(worldCenter: Point2, worldSize: { width: number; height: number }, viewportSize: { width: number; height: number }): Camera {
  const margin = 0.85;
  const zoomX = worldSize.width > 1e-6 ? (viewportSize.width * margin) / worldSize.width : DEFAULT_ZOOM;
  const zoomY = worldSize.height > 1e-6 ? (viewportSize.height * margin) / worldSize.height : DEFAULT_ZOOM;
  const zoom = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, Math.min(zoomX, zoomY)));
  return {
    zoom,
    pan: { x: viewportSize.width / 2 - worldCenter.x * zoom, y: viewportSize.height / 2 - worldCenter.y * zoom },
  };
}

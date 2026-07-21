export interface ChartPoint {
  x: number;
  y: number;
}

export function scaleLinear(domain: [number, number], range: [number, number]) {
  const [d0, d1] = domain;
  const [r0, r1] = range;
  const span = d1 - d0 || 1;
  return (value: number) => r0 + ((value - d0) / span) * (r1 - r0);
}

/** Inverse of scaleLinear - maps a range value (e.g. a pixel x) back into the domain. */
export function invertLinear(domain: [number, number], range: [number, number]) {
  const [d0, d1] = domain;
  const [r0, r1] = range;
  const span = r1 - r0 || 1;
  return (value: number) => d0 + ((value - r0) / span) * (d1 - d0);
}

export function toPolylinePoints(points: ChartPoint[]): string {
  return points.map((p) => `${round(p.x)},${round(p.y)}`).join(' ');
}

function round(n: number): number {
  return Math.round(n * 10) / 10;
}

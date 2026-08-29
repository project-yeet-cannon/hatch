import { describe, expect, it } from 'vitest';
import indexHtml from '../index.html?raw';
import devThemeHtml from '../dev-theme.html?raw';
import mainTsx from './main.tsx?raw';
import packageJson from '../package.json';

/**
 * index.html's boot fallback has no other test surface: it is inline classic JS
 * in a file no build step touches, so nothing type-checks it and nothing
 * imports it. What it protects against is also invisible in review - eight
 * sessions over nine days logged `kiosk index.html parse started` and were
 * never heard from again (Phase 0 of docs/plans/kiosk-graceful-deg.md), and the
 * only symptom was a white rectangle on a wall.
 *
 * So these assert the contract's edges rather than its behaviour: that the
 * screen is still in the document, that main.tsx still takes it down, and that
 * the render-blocking font link has not come back. Each one is a line someone
 * could plausibly delete while tidying, and none of them would fail anything
 * else in the suite.
 *
 * `?raw` rather than node:fs, so this stays inside the app's tsconfig - which
 * deliberately carries no node types - and reads the same bytes Vite would.
 * theme.css is the one file that cannot be read that way: vitest stubs CSS
 * imports to empty by default, query string included, so the font is guarded
 * through package.json instead.
 */
describe('boot fallback', () => {
  it('ships a fallback screen inside #root', () => {
    // Inside #root specifically: React clears that container on mount, so
    // markup placed outside it would survive as a permanent overlay.
    const root = indexHtml.slice(indexHtml.indexOf('<div id="root">'));
    expect(root).toContain('id="aerie-fallback"');
    expect(root.indexOf('id="aerie-fallback"')).toBeLessThan(root.indexOf('</div>\n    <!--'));
  });

  it('renders a clock, a message and a reload control', () => {
    expect(indexHtml).toContain('id="aerie-fallback-clock"');
    expect(indexHtml).toContain('id="aerie-fallback-date"');
    expect(indexHtml).toContain('id="aerie-fallback-message"');
    expect(indexHtml).toContain('id="aerie-fallback-reload"');
  });

  it('falls back to Eastern when the device will not name a timezone', () => {
    expect(indexHtml).toContain('America/New_York');
  });

  it('exposes the dismissal hook that main.tsx calls', () => {
    expect(indexHtml).toContain('window.__aerieDismissFallback = function ()');
    expect(mainTsx).toContain('window.__aerieDismissFallback?.()');
  });

  it('sets the mounted flag before rendering, so the watchdog can lose the race', () => {
    const flag = mainTsx.indexOf('window.__aerieMounted = true');
    // `createRoot(document` rather than `createRoot(`, which also matches the
    // import line at the top of the file.
    const render = mainTsx.indexOf('createRoot(document');
    expect(flag).toBeGreaterThan(-1);
    expect(flag).toBeLessThan(render);
  });

  it('tears down the interval and the watchdog on dismissal', () => {
    const dismiss = indexHtml.slice(indexHtml.indexOf('window.__aerieDismissFallback = function ()'));
    expect(dismiss).toContain('clearInterval(tick)');
    expect(dismiss).toContain('clearTimeout(watchdog)');
  });

  it('logs a watchdog line when the bundle never runs', () => {
    expect(indexHtml).toContain('kiosk mount watchdog fired');
  });

  it('preserves ?source= across the reload button', () => {
    const handler = indexHtml.slice(indexHtml.indexOf("reloadEl.addEventListener('click'"));
    expect(handler).toContain('URLSearchParams(location.search)');
  });
});

describe('fonts', () => {
  it('loads no render-blocking third-party stylesheet', () => {
    // The wall must not need the internet to draw its first frame.
    expect(indexHtml).not.toContain('fonts.googleapis.com');
    expect(devThemeHtml).not.toContain('fonts.googleapis.com');
  });

  it('self-hosts the face it asks for', () => {
    expect(packageJson.dependencies).toHaveProperty('@fontsource-variable/manrope');
  });
});

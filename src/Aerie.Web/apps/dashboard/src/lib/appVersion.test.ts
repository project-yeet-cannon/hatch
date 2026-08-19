import { describe, expect, it } from 'vitest';
import { versionFromAssetUrls } from './appVersion';

// The fixtures below mirror Aerie.Api.Tests/AppVersion/AppVersionServiceTests.cs.
// The two implementations only ever meet in production, so if they disagree the
// kiosks either reload forever or never reload again - keeping the cases
// literally parallel is what makes a divergence show up as a failing test.
const BASE = 'https://kiosk.example.com/apps/dashboard/';
const SCRIPT = '/apps/dashboard/assets/index-BGJobmXl.js';
const STYLESHEET = '/apps/dashboard/assets/index-Dx7nj2H3.css';

describe('versionFromAssetUrls', () => {
  it('joins the hashed assets in ordinal order', () => {
    expect(versionFromAssetUrls([SCRIPT, STYLESHEET], BASE)).toBe('index-BGJobmXl.js+index-Dx7nj2H3.css');
  });

  it('ignores third-party and cross-app references', () => {
    const urls = [
      SCRIPT,
      STYLESHEET,
      'https://fonts.googleapis.com/css2?family=Manrope&display=swap',
      '/apps/dashboard/favicon.svg',
      '/apps/admin/assets/index-ZZZZZZZZ.js',
    ];

    expect(versionFromAssetUrls(urls, BASE)).toBe('index-BGJobmXl.js+index-Dx7nj2H3.css');
  });

  it('is order independent', () => {
    expect(versionFromAssetUrls([STYLESHEET, SCRIPT], BASE)).toBe(versionFromAssetUrls([SCRIPT, STYLESHEET], BASE));
  });

  it('deduplicates repeated references', () => {
    // A modulepreload names the same file the script tag does.
    expect(versionFromAssetUrls([SCRIPT, SCRIPT, STYLESHEET], BASE)).toBe('index-BGJobmXl.js+index-Dx7nj2H3.css');
  });

  // The DOM hands back absolute URLs from el.src but the raw attribute from
  // getAttribute; both have to reduce to the same token as the server's parse
  // of the root-relative form it wrote into index.html.
  it('treats absolute and root-relative forms as the same asset', () => {
    const absolute = `https://kiosk.example.com${SCRIPT}`;

    expect(versionFromAssetUrls([absolute, STYLESHEET], BASE)).toBe('index-BGJobmXl.js+index-Dx7nj2H3.css');
  });

  it('is empty for a dev server document', () => {
    expect(versionFromAssetUrls(['/src/main.tsx', null, undefined], BASE)).toBe('');
  });

  it('ignores nested paths, matching the server regex which cannot cross a slash', () => {
    expect(versionFromAssetUrls(['/apps/dashboard/assets/chunks/deep-ABC123.js'], BASE)).toBe('');
  });
});

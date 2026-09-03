import { describe, expect, it } from 'vitest';
import { isPlainClick } from './pointer';

describe('isPlainClick', () => {
  it('is a plain left click with nothing held', () => {
    expect(isPlainClick({})).toBe(true);
    expect(isPlainClick({ button: 0 })).toBe(true);
  });

  /* Each of these is a gesture aimed at the link: a new tab, a new window, a
     download. Taking them over is how a board stops being able to hand somebody
     a URL. */
  it('leaves a modified click to the browser', () => {
    expect(isPlainClick({ metaKey: true })).toBe(false);
    expect(isPlainClick({ ctrlKey: true })).toBe(false);
    expect(isPlainClick({ shiftKey: true })).toBe(false);
    expect(isPlainClick({ altKey: true })).toBe(false);
  });

  it('leaves the middle button alone', () => {
    expect(isPlainClick({ button: 1 })).toBe(false);
  });
});

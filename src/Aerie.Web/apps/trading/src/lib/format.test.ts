import { describe, expect, it } from 'vitest';
import { pickerHref } from './pickerHref';
import { betterWhenPositive, describeParams, duration, formatMetric, metricLabel, percent, when } from './format';

/**
 * The pure parts: how a figure is read, and where the app picker is.
 *
 * The screens themselves are the operator's to check in a browser. What is
 * worth asserting here is the arithmetic and the derivations - the places a
 * quiet mistake produces a page that looks right and says something wrong.
 */

describe('formatting a figure', () => {
  it('reads a return as a percentage and a Sharpe as a ratio', () => {
    // The same string, formatted by what the metric *is* rather than by its
    // shape: 0.42 is 42% of a return and 0.42 of a Sharpe.
    expect(formatMetric('total_return', '0.4231')).toBe('42.31%');
    expect(formatMetric('sharpe', '0.4231')).toBe('0.42');
  });

  it('keeps a metric this build has never heard of', () => {
    // A later phase adds metrics, and a formatter that dropped the ones it had
    // no rule for would hide them until somebody remembered to add a label.
    expect(formatMetric('something_new', '1.5')).toBe('1.50');
    expect(metricLabel('something_new')).toBe('something new');
  });

  it('knows which direction each metric improves in', () => {
    expect(betterWhenPositive('sharpe')).toBe(true);
    // A drawdown is better when it is smaller, so a positive one is not good
    // news dressed in green.
    expect(betterWhenPositive('max_drawdown')).toBe(false);
    // And a trade count is neither: the question does not apply, and a colour
    // here would be a claim the number does not make.
    expect(betterWhenPositive('trade_count')).toBeUndefined();
  });

  it('does not print Invalid Date for a run that has not finished', () => {
    expect(when(null)).toBe('—');
    expect(when('not a date')).toBe('—');
  });

  it('says "defaults" for a parameter set with nothing in it', () => {
    // An empty object is a real answer - a strategy with no swept parameters
    // runs at every default - and rendering it as `{}` would read as missing.
    expect(describeParams({})).toBe('defaults');
    expect(describeParams(null)).toBe('—');
    expect(describeParams({ fast: 5, slow: 20 })).toBe('fast 5, slow 20');
  });

  it('scales a duration to something a person reads', () => {
    expect(duration(null)).toBe('—');
    expect(duration(340)).toBe('340 ms');
    expect(duration(4200)).toBe('4.2 s');
    expect(duration(125_000)).toBe('2m 5s');
  });

  it('does not lose precision to a float on the way to a percentage', () => {
    expect(percent('0.1', 1)).toBe('10.0%');
  });
});

describe('finding the app picker', () => {
  // The base domain is not a value this repository may hold (docs/ethos.md),
  // so it is read off the host the page was served from - the same derivation
  // the picker makes in the other direction.
  it('borrows the domain this app is served from', () => {
    expect(pickerHref('trading.example.org')).toBe('https://home.example.org/');
    expect(pickerHref('trading.deep.example.org')).toBe('https://home.deep.example.org/');
  });

  it('has nowhere to point when there is no domain to borrow', () => {
    // An IPv4 literal has labels, but they are octets: `home.2.3.4` is not an
    // address. A single label and a bare IPv6 are the same problem.
    expect(pickerHref('192.168.1.10')).toBe('/');
    expect(pickerHref('localhost')).toBe('/');
    expect(pickerHref('fe80::1')).toBe('/');
  });
});

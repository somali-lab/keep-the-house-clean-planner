import { describe, expect, it } from 'vitest';
import { formatFactor, formatMinutes, formatPercent, niceMax, seriesColor, ticks } from './scale.ts';

describe('scale', () => {
  it.each([
    [0, 60],
    [7, 10],
    [18, 20],
    [165, 200],
    [240, 250],
    [285, 500],
    [1000, 1000],
  ])('niceMax(%i) = %i', (max, expected) => {
    expect(niceMax(max)).toBe(expected);
  });

  it('makes clean ticks from zero', () => {
    expect(ticks(285)).toEqual([0, 125, 250, 375, 500]);
    expect(ticks(165)).toEqual([0, 50, 100, 150, 200]);
  });

  it('formats Dutch numbers', () => {
    expect(formatMinutes(1285)).toBe('1.285 min');
    expect(formatPercent(0.6)).toBe('60%');
    expect(formatPercent(null)).toBe('—');
    expect(formatFactor(15 / 14)).toBe('×1,1');
  });

  it('assigns series colors by fixed slot', () => {
    expect(seriesColor(0)).toBe('var(--series-1)');
    expect(seriesColor(2)).toBe('var(--series-3)');
    expect(seriesColor(20)).toBe('var(--series-8)');
  });
});

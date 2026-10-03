import { describe, expect, it } from 'vitest';
import { defaultPointsForDuration, formatCents, MAX_CENTS_PER_POINT, MAX_TASK_POINTS, MIN_CENTS_PER_POINT, MIN_TASK_POINTS, isTwoDecimalCurrency, pointsToCents } from './points.ts';

describe('defaultPointsForDuration', () => {
  it.each([
    [1, 1],
    [5, 5],
    [10, 10],
    [30, 30],
    [45, 45],
    [60, 60],
    [999, 999],
    [1000, 1000],
    [1001, 1000],
    [5000, 1000],
  ])('%d minutes earn %d points', (minutes, points) => {
    expect(defaultPointsForDuration(minutes)).toBe(points);
  });

  it('never leaves the bounds, also for degenerate input', () => {
    for (const minutes of [0, -5, Number.NaN, Number.POSITIVE_INFINITY]) {
      const points = defaultPointsForDuration(minutes);
      expect(points).toBeGreaterThanOrEqual(1);
      expect(points).toBeLessThanOrEqual(MAX_TASK_POINTS);
    }
  });

  it('keeps the bounds of a task at 0..1000', () => {
    expect([MIN_TASK_POINTS, MAX_TASK_POINTS]).toEqual([0, 1000]);
  });
});

describe('points to money (requirements 4.12)', () => {
  it('multiplies whole points with whole cents, never rounding', () => {
    expect(pointsToCents(7, 25)).toBe(175);
    expect(pointsToCents(0, 25)).toBe(0);
    expect(pointsToCents(-4, 10)).toBe(-40);
    expect(pointsToCents(1000, MAX_CENTS_PER_POINT)).toBe(10_000_000);
    expect(MIN_CENTS_PER_POINT).toBe(0);
  });

  it('knows which currencies have exactly two fraction digits', () => {
    for (const code of ['EUR', 'USD', 'GBP', 'CHF']) expect(isTwoDecimalCurrency(code)).toBe(true);
    for (const code of ['JPY', 'KWD', 'BHD', 'not a code', '']) expect(isTwoDecimalCurrency(code)).toBe(false);
  });

  it('formats cents in the currency and locale with Intl', () => {
    expect(formatCents(175, 'EUR', 'en-GB')).toBe('€1.75');
    expect(formatCents(175, 'EUR', 'nl-NL').replaceAll('\u00a0', ' ')).toBe('€ 1,75');
    expect(formatCents(-5, 'USD', 'en-US')).toBe('-$0.05');
    expect(formatCents(150, 'JPY', 'en-US')).toContain('¥');
  });
});

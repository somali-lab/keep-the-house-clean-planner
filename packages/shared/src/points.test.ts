import { describe, expect, it } from 'vitest';
import { defaultPointsForDuration, MAX_TASK_POINTS, MIN_TASK_POINTS } from './points.ts';

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

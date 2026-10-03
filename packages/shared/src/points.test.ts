import { describe, expect, it } from 'vitest';
import { defaultPointsForDuration, MAX_TASK_POINTS, MIN_TASK_POINTS } from './points.ts';

describe('defaultPointsForDuration', () => {
  it.each([
    [1, 1],
    [5, 1],
    [10, 1],
    [11, 2],
    [20, 2],
    [30, 3],
    [31, 4],
    [45, 5],
    [60, 6],
    [999, 100],
    [1000, 100],
    [5000, 100],
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

  it('keeps the bounds of a task at 0..100', () => {
    expect([MIN_TASK_POINTS, MAX_TASK_POINTS]).toEqual([0, 100]);
  });
});

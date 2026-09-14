import type { Interval } from '@huishoudplanner/shared';

/** Intervals live in settings, so new keys (e.g. 'year') need no code change. */
export function findInterval(intervals: Interval[], key: string): Interval | undefined {
  return intervals.find((i) => i.key === key);
}

/** Keys present before but missing after an intervals update. */
export function removedIntervalKeys(before: Interval[], after: Interval[]): string[] {
  const kept = new Set(after.map((i) => i.key));
  return before.map((i) => i.key).filter((key) => !kept.has(key));
}

import { cycleEnd, cycleIndexFor, cycleStart } from '@huishoudplanner/shared/cycle';
import { addDays, mondayOf } from '@huishoudplanner/shared/time';
import type { StatsPeriod } from './api.ts';

export interface PointsRange {
  from: string;
  to: string;
}

/**
 * The calendar days the points follow for a statistics period, both included: the current week and
 * the weeks before it, or the current cycle and the cycles before it (never earlier than the first cycle).
 * This is the same window the other statistics reports use.
 */
export function pointsRange(period: StatsPeriod, anchor: string, todayKey: string): PointsRange {
  if (period.unit === 'weeks') {
    const monday = mondayOf(todayKey);
    return { from: addDays(monday, -(period.count - 1) * 7), to: addDays(monday, 6) };
  }
  const current = Math.max(0, cycleIndexFor(todayKey, anchor));
  return { from: cycleStart(Math.max(0, current - period.count + 1), anchor), to: cycleEnd(current, anchor) };
}

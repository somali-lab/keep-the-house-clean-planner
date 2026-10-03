import { cycleEnd, cycleIndexFor, cycleStart } from '@huishoudplanner/shared/cycle';
import { addDays, isoWeek, mondayOf } from '@huishoudplanner/shared/time';
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

/** The message and values that label a week or cycle bonus entry (ADR-0012). */
export interface BonusLabel {
  key:
    | 'stats.points.bonus.weekDone'
    | 'stats.points.bonus.weekOnTime'
    | 'stats.points.bonus.cycleDone'
    | 'stats.points.bonus.cycleOnTime';
  /** ISO week number of the first day of the period; used by the week messages. */
  week: number;
  /** First and last day of the period, as day keys; used by the cycle messages. */
  from: string;
  to: string;
}

const BONUS_LABEL_KEY = {
  bonus_week_done: 'stats.points.bonus.weekDone',
  bonus_week_ontime: 'stats.points.bonus.weekOnTime',
  bonus_cycle_done: 'stats.points.bonus.cycleDone',
  bonus_cycle_ontime: 'stats.points.bonus.cycleOnTime',
} as const satisfies Record<string, BonusLabel['key']>;

/**
 * What a ledger entry says about its period, or null for an entry that is not a bonus. A bonus is
 * dated on the last day of its period, and `periodStart` is the first day.
 */
export function bonusLabel(entry: { kind: string; periodStart: string | null; date: string }): BonusLabel | null {
  if (!(entry.kind in BONUS_LABEL_KEY) || entry.periodStart === null) return null;
  return {
    key: BONUS_LABEL_KEY[entry.kind as keyof typeof BONUS_LABEL_KEY],
    week: isoWeek(entry.periodStart).week,
    from: entry.periodStart,
    to: entry.date,
  };
}

const BONUS_KIND_OF_KEY = new Set(Object.keys(BONUS_LABEL_KEY));

/**
 * The label of a bonus that is only known by its ledger key, `<kind>:<personId>:<periodStart>`, as the
 * history of a reconciliation lists it. A week ends six days and a cycle 27 days after its first day.
 */
export function bonusLabelOfKey(key: string): BonusLabel | null {
  const [kind, , periodStart] = key.split(':');
  if (!kind || !periodStart || !BONUS_KIND_OF_KEY.has(kind) || !/^\d{4}-\d{2}-\d{2}$/.test(periodStart)) return null;
  const days = kind.startsWith('bonus_week') ? 6 : 27;
  return bonusLabel({ kind: kind as keyof typeof BONUS_LABEL_KEY, periodStart, date: addDays(periodStart, days) });
}

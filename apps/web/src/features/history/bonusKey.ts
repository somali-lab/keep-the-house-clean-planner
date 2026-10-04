import { addDays } from '@/lib/dayKey';
import { bonusLabel, type BonusLabel } from '../stats/pointsModel.ts';

const BONUS_KINDS = new Set(['bonus_week_done', 'bonus_week_ontime', 'bonus_cycle_done', 'bonus_cycle_ontime']);

/** What the label needs from the server: the ISO week number of a day (the calendar) and the length of a cycle (the limits). */
export interface BonusKeyContext {
  /** The ISO week number of a day, or null while the calendar has not answered for it. */
  weekOf?: (dayKey: string) => number | null;
  cycleDays?: number;
}

/**
 * The label of a bonus that is only known by its ledger key, `<kind>:<personId>:<periodStart>`, as the history of a reconciliation
 * lists it. A week ends six days after its first day and a cycle one cycle length (from the limits) after it; the number of a week
 * is the ISO week of the calendar of the server. Null for anything else, and while the week or the cycle length is not known yet.
 */
export function bonusLabelOfKey(key: string, { weekOf, cycleDays }: BonusKeyContext): BonusLabel | null {
  const [kind, , periodStart] = key.split(':');
  if (!kind || !periodStart || !BONUS_KINDS.has(kind) || !/^\d{4}-\d{2}-\d{2}$/.test(periodStart)) return null;
  if (kind.startsWith('bonus_week')) {
    const week = weekOf?.(periodStart) ?? null;
    return week === null ? null : bonusLabel({ kind, periodStart, date: addDays(periodStart, 6) }, week);
  }
  // The cycle messages show the dates of the period, not a week number.
  return cycleDays === undefined ? null : bonusLabel({ kind, periodStart, date: addDays(periodStart, cycleDays - 1) }, 0);
}

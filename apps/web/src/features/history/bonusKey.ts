import { addDays, isoWeek } from '@huishoudplanner/shared/time';
import { bonusLabel, type BonusLabel } from '../stats/pointsModel.ts';

const BONUS_KINDS = new Set(['bonus_week_done', 'bonus_week_ontime', 'bonus_cycle_done', 'bonus_cycle_ontime']);

/**
 * The label of a bonus that is only known by its ledger key, `<kind>:<personId>:<periodStart>`, as the history of a reconciliation
 * lists it. A week ends six days and a cycle 27 days after its first day. The history still reads the audit log of the Node server and
 * works the week number out itself; it moves to the calendar of API v2 with its own slice (plan 7.5).
 */
export function bonusLabelOfKey(key: string): BonusLabel | null {
  const [kind, , periodStart] = key.split(':');
  if (!kind || !periodStart || !BONUS_KINDS.has(kind) || !/^\d{4}-\d{2}-\d{2}$/.test(periodStart)) return null;
  const days = kind.startsWith('bonus_week') ? 6 : 27;
  return bonusLabel({ kind, periodStart, date: addDays(periodStart, days) }, isoWeek(periodStart).week);
}

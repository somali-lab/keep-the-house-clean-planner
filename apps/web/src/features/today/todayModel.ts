import type { Occurrence } from '../../api/index.ts';
import { getLocale } from '../../i18n/runtime.ts';

/** How far back the Today view looks for overdue items (two cycles). */
export const OVERDUE_LOOKBACK_DAYS = 56;

export interface TodayGroups {
  /** 1. My open occurrences for today. */
  mine: Occurrence[];
  /** 2. Unclaimed ("wie pakt 'm") open occurrences for today. */
  unclaimed: Occurrence[];
  /** 3. Open occurrences for today assigned to someone else. */
  others: Occurrence[];
  /** 4. Open occurrences from before today. */
  overdue: Occurrence[];
  /** Done or skipped today, so undo stays reachable later. */
  finished: Occurrence[];
}

/** Section order from plan §1.4. */
export function groupToday(
  occurrences: Occurrence[],
  profileId: string,
  todayKey: string,
  overdueFrom = '',
): TodayGroups {
  const sorted = [...occurrences].sort(
    (a, b) =>
      a.date.localeCompare(b.date) || a.taskNameSnapshot.localeCompare(b.taskNameSnapshot, getLocale()),
  );
  const groups: TodayGroups = { mine: [], unclaimed: [], others: [], overdue: [], finished: [] };
  for (const occ of sorted) {
    if (occ.date > todayKey) continue;
    if (occ.status !== 'open') {
      if (occ.date === todayKey) groups.finished.push(occ);
      continue;
    }
    if (occ.date < todayKey) {
      if (occ.date >= overdueFrom) groups.overdue.push(occ);
    }
    else if (occ.assigneeId === profileId) groups.mine.push(occ);
    else if (occ.assigneeId === null) groups.unclaimed.push(occ);
    else groups.others.push(occ);
  }
  return groups;
}

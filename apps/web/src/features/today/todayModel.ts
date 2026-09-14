import type { OccurrenceView } from '@huishoudplanner/shared';
import { getLocale } from '../../i18n/runtime.ts';

/** How far back the Today view looks for overdue items (two cycles). */
export const OVERDUE_LOOKBACK_DAYS = 56;

/** 'YYYY-MM-DD' of an instant in a timezone, without pulling a date library into the bundle. */
export function dayKeyInZone(now: Date, timeZone: string): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(now);
  const get = (type: string) => parts.find((p) => p.type === type)?.value ?? '';
  return `${get('year')}-${get('month')}-${get('day')}`;
}

/** Calendar arithmetic on day keys. */
export function addDaysKey(dayKey: string, days: number): string {
  const [y, m, d] = dayKey.split('-').map(Number) as [number, number, number];
  return new Date(Date.UTC(y, m - 1, d + days)).toISOString().slice(0, 10);
}

export interface TodayGroups {
  /** 1. My open occurrences for today. */
  mine: OccurrenceView[];
  /** 2. Unclaimed ("wie pakt 'm") open occurrences for today. */
  unclaimed: OccurrenceView[];
  /** 3. Open occurrences for today assigned to someone else. */
  others: OccurrenceView[];
  /** 4. Open occurrences from before today. */
  overdue: OccurrenceView[];
  /** Done or skipped today, so undo stays reachable later. */
  finished: OccurrenceView[];
}

/** Section order from plan §1.4. */
export function groupToday(occurrences: OccurrenceView[], profileId: string, todayKey: string): TodayGroups {
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
    if (occ.date < todayKey) groups.overdue.push(occ);
    else if (occ.assigneeId === profileId) groups.mine.push(occ);
    else if (occ.assigneeId === null) groups.unclaimed.push(occ);
    else groups.others.push(occ);
  }
  return groups;
}

import type { OccurrenceView } from '@huishoudplanner/shared';
import { getLocale } from '../../i18n/runtime.ts';
import { mondayOfDay } from '../export/exportModel.ts';
import { addDaysKey } from '../today/todayModel.ts';

function dayDate(dayKey: string): Date {
  return new Date(`${dayKey}T12:00:00Z`);
}

function monthName(month: number): string {
  return new Intl.DateTimeFormat(getLocale(), { month: 'short', timeZone: 'UTC' })
    .format(new Date(Date.UTC(2024, month - 1, 1)))
    .replace('.', '');
}

export function weekdayOfDay(dayKey: string): number {
  const [y, m, d] = dayKey.split('-').map(Number) as [number, number, number];
  return new Date(Date.UTC(y, m - 1, d)).getUTCDay();
}

/** "di 8 sep" */
export function shortDay(dayKey: string): string {
  return new Intl.DateTimeFormat(getLocale(), {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    timeZone: 'UTC',
  })
    .format(dayDate(dayKey))
    .replaceAll('.', '');
}

/** "dinsdag 8 sep" */
export function longDay(dayKey: string): string {
  return new Intl.DateTimeFormat(getLocale(), {
    weekday: 'long',
    day: 'numeric',
    month: 'short',
    timeZone: 'UTC',
  })
    .format(dayDate(dayKey))
    .replaceAll('.', '');
}

/** Compact Dutch range label, for example "14 – 20 sep 2026". */
export function weekRangeLabel(from: string, to: string): string {
  const [fromYear, fromMonth, fromDay] = from.split('-').map(Number) as [number, number, number];
  const [toYear, toMonth, toDay] = to.split('-').map(Number) as [number, number, number];
  if (fromYear === toYear && fromMonth === toMonth) {
    return `${fromDay} – ${toDay} ${monthName(toMonth)} ${toYear}`;
  }
  if (fromYear === toYear) {
    return `${fromDay} ${monthName(fromMonth)} – ${toDay} ${monthName(toMonth)} ${toYear}`;
  }
  return `${fromDay} ${monthName(fromMonth)} ${fromYear} – ${toDay} ${monthName(toMonth)} ${toYear}`;
}

export function weekdayName(weekday: number): string {
  if (weekday < 0 || weekday > 6) return '';
  return new Intl.DateTimeFormat(getLocale(), { weekday: 'long', timeZone: 'UTC' }).format(
    new Date(Date.UTC(2024, 0, 7 + weekday)),
  );
}

/** Monday..Sunday of the week containing the day. */
export function weekDays(anyDay: string): string[] {
  const monday = mondayOfDay(anyDay);
  return Array.from({ length: 7 }, (_, i) => addDaysKey(monday, i));
}

/** Three days before the center day, the center day, and eight days after it. */
export function overviewDays(centerDay: string): string[] {
  return Array.from({ length: 12 }, (_, index) => addDaysKey(centerDay, index - 3));
}

export interface WeekDay {
  dayKey: string;
  items: OccurrenceView[];
}

export function groupByDay(occurrences: OccurrenceView[], days: string[]): WeekDay[] {
  return days.map((dayKey) => ({
    dayKey,
    items: occurrences
      .filter((o) => o.date === dayKey)
      .sort((a, b) => a.taskNameSnapshot.localeCompare(b.taskNameSnapshot, getLocale())),
  }));
}

/** Local view of a move, as the server will store it. */
export function movedTo(occ: OccurrenceView, date: string): OccurrenceView {
  return { ...occ, date, movedFrom: date === occ.plannedDate ? null : occ.plannedDate };
}

export const dayDropId = (dayKey: string) => `day:${dayKey}`;
export const occurrenceDragId = (id: string) => `occ:${id}`;

export function parseDayDropId(id: string): string | null {
  const match = /^day:(\d{4}-\d{2}-\d{2})$/.exec(id);
  return match ? match[1]! : null;
}

export function parseOccurrenceDragId(id: string): string | null {
  const match = /^occ:(.+)$/.exec(id);
  return match ? match[1]! : null;
}

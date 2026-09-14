import type { Cycle } from '@huishoudplanner/shared';
import type { Language } from '../../i18n/runtime.ts';
import { addDaysKey } from '../today/todayModel.ts';

type CycleRange = Pick<Cycle, 'startDate' | 'endDate'>;

function utcDate(dayKey: string): Date {
  const [y, m, d] = dayKey.split('-').map(Number) as [number, number, number];
  return new Date(Date.UTC(y, m - 1, d));
}

/** ISO week label ('2026-W38') of a day key, without a date library in the bundle. */
export function isoWeekLabel(dayKey: string): string {
  const date = utcDate(dayKey);
  const weekday = date.getUTCDay() || 7;
  date.setUTCDate(date.getUTCDate() + 4 - weekday); // Thursday decides the ISO year
  const yearStart = Date.UTC(date.getUTCFullYear(), 0, 1);
  const week = Math.ceil(((date.getTime() - yearStart) / 86_400_000 + 1) / 7);
  return `${date.getUTCFullYear()}-W${String(week).padStart(2, '0')}`;
}

export function mondayOfDay(dayKey: string): string {
  const weekday = utcDate(dayKey).getUTCDay();
  return addDaysKey(dayKey, -((weekday + 6) % 7));
}

export function isGenerated(dayKey: string, cycles: CycleRange[]): boolean {
  return cycles.some((c) => c.startDate <= dayKey && dayKey <= c.endDate);
}

export interface WeekOption {
  label: string;
  monday: string;
  sunday: string;
}

export const WEEK_OPTION_COUNT = 12;

/** The current week and the following weeks, Monday-based. */
export function weekOptions(todayKey: string, count = WEEK_OPTION_COUNT): WeekOption[] {
  const first = mondayOfDay(todayKey);
  return Array.from({ length: count }, (_, i) => {
    const monday = addDaysKey(first, i * 7);
    return { label: isoWeekLabel(monday), monday, sunday: addDaysKey(monday, 6) };
  });
}

/** Every week of the range must lie in a generated cycle (cycles align with Monday-based weeks). */
export function rangeGenerated(startMonday: string, weeks: number, cycles: CycleRange[]): boolean {
  return Array.from({ length: weeks }, (_, i) => addDaysKey(startMonday, i * 7)).every((m) => isGenerated(m, cycles));
}

export type ExportRange = 'day' | '1' | '2' | '4' | 'due';

export interface ExportChoice {
  range: ExportRange;
  startWeek: string;
  date: string;
  orientation: 'portrait' | 'landscape';
  totals: boolean;
  language?: Language;
}

export function weeksOf(range: ExportRange): number {
  return range === '1' ? 1 : range === '2' ? 2 : range === '4' ? 4 : 0;
}

export function exportUrl(choice: ExportChoice): string {
  const language = choice.language === 'en' ? '&language=en' : '';
  if (choice.range === 'day') return `/api/export/pdf/day?date=${choice.date}${language}`;
  if (choice.range === 'due') return choice.language === 'en' ? '/api/export/pdf/due?language=en' : '/api/export/pdf/due';
  const weeks = weeksOf(choice.range);
  const params = new URLSearchParams({
    fromWeek: choice.startWeek,
    weeks: String(weeks),
    orientation: weeks === 2 ? choice.orientation : 'portrait',
    totals: String(choice.totals),
  });
  if (choice.language === 'en') params.set('language', 'en');
  return `/api/export/pdf?${params.toString()}`;
}

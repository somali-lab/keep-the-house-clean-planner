import type { Language } from '../../i18n/runtime.ts';
import { addDays } from '@/lib/dayKey';
import type { CalendarDay } from '../../api/v2/queries.ts';
import type { Cycle } from './api.ts';

type CycleRange = Pick<Cycle, 'startDate' | 'endDate'>;

export function isGenerated(dayKey: string, cycles: CycleRange[]): boolean {
  return cycles.some((c) => c.startDate <= dayKey && dayKey <= c.endDate);
}

export interface WeekOption {
  label: string;
  monday: string;
  sunday: string;
}

export const WEEK_OPTION_COUNT = 12;

/** The first and last day the calendar has to answer for `weekOptions`: the Monday of the current week lies at most six days back. */
export function weekOptionsRange(todayKey: string, count = WEEK_OPTION_COUNT): { from: string; to: string } {
  return { from: addDays(todayKey, -6), to: addDays(todayKey, count * 7) };
}

/**
 * The current week and the following weeks, Monday-based. The Monday of the week and the ISO week labels come from the
 * server's calendar (`GET /api/v2/calendar`, range `weekOptionsRange`); a week the calendar does not know is left out.
 */
export function weekOptions(todayKey: string, calendar: ReadonlyMap<string, CalendarDay>, count = WEEK_OPTION_COUNT): WeekOption[] {
  const first = calendar.get(todayKey)?.weekStart;
  if (first === undefined) return [];
  return Array.from({ length: count }, (_, i) => {
    const monday = addDays(first, i * 7);
    const label = calendar.get(monday)?.isoWeek;
    return label === undefined ? [] : [{ label, monday, sunday: addDays(monday, 6) }];
  }).flat();
}

/** Every week of the range must lie in a generated cycle (cycles align with Monday-based weeks). */
export function rangeGenerated(startMonday: string, weeks: number, cycles: CycleRange[]): boolean {
  return Array.from({ length: weeks }, (_, i) => addDays(startMonday, i * 7)).every((m) => isGenerated(m, cycles));
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

const PDF_BASE = '/api/v2/export/pdf';

/**
 * The address of a PDF sheet. The sheets need no profile (the endpoints are open), so a plain link downloads them and the browser
 * streams the file. Optional keys are left out; English is the only language that is named (Dutch is the server default).
 */
export function exportUrl(choice: ExportChoice): string {
  const language = choice.language === 'en' ? '&language=en' : '';
  if (choice.range === 'day') return `${PDF_BASE}/day?date=${choice.date}${language}`;
  if (choice.range === 'due') return choice.language === 'en' ? `${PDF_BASE}/due?language=en` : `${PDF_BASE}/due`;
  const weeks = weeksOf(choice.range);
  const params = new URLSearchParams({
    fromWeek: choice.startWeek,
    weeks: String(weeks),
    orientation: weeks === 2 ? choice.orientation : 'portrait',
    totals: String(choice.totals),
  });
  if (choice.language === 'en') params.set('language', 'en');
  return `${PDF_BASE}/schedule?${params.toString()}`;
}

/** The list of all tasks, grouped by room. */
export function tasksPdfUrl(language?: Language): string {
  return language === 'en' ? `${PDF_BASE}/tasks?language=en` : `${PDF_BASE}/tasks`;
}

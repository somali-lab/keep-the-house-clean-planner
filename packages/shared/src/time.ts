import { DateTime } from 'luxon';

/**
 * All time conversion in the app goes through this module.
 * A "day key" is a calendar date 'YYYY-MM-DD' in the app timezone.
 * Weekdays in the domain use 0=Sunday..6=Saturday; grids show Monday first.
 */

export const APP_TIMEZONE = 'Europe/Amsterdam';

export type DayKey = string;

const DAY_KEY_RE = /^\d{4}-\d{2}-\d{2}$/;

export function isDayKey(value: string): boolean {
  return DAY_KEY_RE.test(value) && parseDayKey(value).isValid;
}

function parseDayKey(key: DayKey, zone = 'UTC'): DateTime {
  return DateTime.fromISO(key, { zone });
}

function assertDayKey(key: DayKey): DateTime {
  const dt = DAY_KEY_RE.test(key) ? parseDayKey(key) : DateTime.invalid('format');
  if (!dt.isValid) throw new RangeError(`Invalid day key: ${key}`);
  return dt;
}

/** Day key of the given instant (default: now) in the timezone. */
export function today(tz: string = APP_TIMEZONE, now: Date = new Date()): DayKey {
  return toDayKey(now, tz);
}

/** Calendar date of an instant in the timezone. */
export function toDayKey(date: Date, tz: string = APP_TIMEZONE): DayKey {
  const dt = DateTime.fromJSDate(date, { zone: tz });
  if (!dt.isValid) throw new RangeError(`Invalid date or timezone: ${String(date)} / ${tz}`);
  return dt.toISODate() as DayKey;
}

/** Instant of local midnight (00:00 in tz) of the day key. */
export function fromDayKey(key: DayKey, tz: string = APP_TIMEZONE): Date {
  assertDayKey(key);
  const dt = parseDayKey(key, tz).startOf('day');
  if (!dt.isValid) throw new RangeError(`Invalid timezone: ${tz}`);
  return dt.toJSDate();
}

/** Calendar arithmetic on day keys; independent of DST. */
export function addDays(key: DayKey, days: number): DayKey {
  return assertDayKey(key).plus({ days }).toISODate() as DayKey;
}

/** Whole calendar days from `from` to `to` (negative if `to` is earlier). */
export function daysBetween(from: DayKey, to: DayKey): number {
  return Math.round(assertDayKey(to).diff(assertDayKey(from), 'days').days);
}

/** 0=Sunday..6=Saturday. */
export function weekdaySun0(key: DayKey): number {
  return assertDayKey(key).weekday % 7;
}

/** 0=Monday..6=Sunday. */
export function weekdayMon0(key: DayKey): number {
  return assertDayKey(key).weekday - 1;
}

export function sun0ToMon0(weekday: number): number {
  return (weekday + 6) % 7;
}

export function mon0ToSun0(weekday: number): number {
  return (weekday + 1) % 7;
}

export function isMonday(key: DayKey): boolean {
  return isDayKey(key) && weekdayMon0(key) === 0;
}

export function isWeekend(key: DayKey): boolean {
  const wd = weekdaySun0(key);
  return wd === 0 || wd === 6;
}

export interface IsoWeek {
  year: number;
  week: number;
}

export function isoWeek(key: DayKey): IsoWeek {
  const dt = assertDayKey(key);
  return { year: dt.weekYear, week: dt.weekNumber };
}

/** e.g. '2026-W38'. */
export function isoWeekLabel(key: DayKey): string {
  const { year, week } = isoWeek(key);
  return `${year}-W${String(week).padStart(2, '0')}`;
}

const ISO_WEEK_RE = /^(\d{4})-W(\d{2})$/;

/** Monday of an ISO week label, or null if the label is invalid. */
export function mondayOfIsoWeek(label: string): DayKey | null {
  const match = ISO_WEEK_RE.exec(label);
  if (!match) return null;
  const dt = DateTime.fromObject(
    { weekYear: Number(match[1]), weekNumber: Number(match[2]), weekday: 1 },
    { zone: 'UTC' },
  );
  if (!dt.isValid || isoWeekLabel(dt.toISODate() as DayKey) !== label) return null;
  return dt.toISODate() as DayKey;
}

/** Monday of the ISO week containing the day. */
export function mondayOf(key: DayKey): DayKey {
  return addDays(key, -weekdayMon0(key));
}

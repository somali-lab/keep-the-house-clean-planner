import { addDays } from '@/lib/dayKey';
import type { CalendarDay } from '../../api/v2/queries.ts';
import type { StatsPeriod } from './api.ts';

export interface PointsRange {
  from: string;
  to: string;
}

/**
 * The calendar days the points follow for a statistics period, both included: the current week and the weeks before it, or the
 * current cycle and the cycles before it (never earlier than the first cycle). This is the same window the other statistics reports
 * use. Where today falls comes from the server's calendar (`GET /calendar`); this only lays the requested number of weeks or cycles
 * back from it. `cycleDays` is the length of a cycle from the limits of the server.
 */
export function pointsRange(period: StatsPeriod, today: Pick<CalendarDay, 'weekStart' | 'weekIndex' | 'cycleIndex'>, cycleDays: number): PointsRange {
  if (period.unit === 'weeks') {
    return { from: addDays(today.weekStart, -(period.count - 1) * 7), to: addDays(today.weekStart, 6) };
  }
  const currentStart = addDays(today.weekStart, -today.weekIndex * 7);
  const startOf = (cycleIndex: number) => addDays(currentStart, (cycleIndex - today.cycleIndex) * cycleDays);
  const current = Math.max(0, today.cycleIndex);
  return { from: startOf(Math.max(0, current - period.count + 1)), to: addDays(startOf(current), cycleDays - 1) };
}

/** The week number in the ISO week label of the calendar (`2026-W38`), or null for anything else. */
export function isoWeekNumber(label: string | undefined): number | null {
  const match = /-W(\d{2})$/.exec(label ?? '');
  return match ? Number(match[1]) : null;
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
 * dated on the last day of its period, and `periodStart` is the first day; `week` is the ISO week number of that first day, which
 * the caller reads from the calendar of the server.
 */
export function bonusLabel(entry: { kind: string; periodStart: string | null; date: string }, week: number): BonusLabel | null {
  if (!(entry.kind in BONUS_LABEL_KEY) || entry.periodStart === null) return null;
  return {
    key: BONUS_LABEL_KEY[entry.kind as keyof typeof BONUS_LABEL_KEY],
    week,
    from: entry.periodStart,
    to: entry.date,
  };
}

/** What is typed in the redeem dialog. */
export interface RedeemForm {
  points: string;
  note: string;
}

export type RedeemField = 'points' | 'note';
export type RedeemErrors = Partial<Record<RedeemField, 'redeem.error.points' | 'redeem.error.balance' | 'redeem.error.note'>>;
export type RedeemResult = { ok: true; points: number; note: string } | { ok: false; errors: RedeemErrors };

/** A whole number of points from the text of a field, or null when it is empty, fractional, negative or not a number. */
export function parseWholePoints(text: string): number | null {
  const trimmed = text.trim();
  return /^\d{1,9}$/.test(trimmed) ? Number(trimmed) : null;
}

/**
 * Checks the redeem form against the balance that is available: at least 1 point, at most the balance
 * (the server refuses a booking that would make the balance negative, requirements 4.12) and a note of at most
 * `maxNoteLength` characters (a limit of the server). The note is trimmed, like the server does.
 */
export function buildRedemption(form: RedeemForm, balance: number, maxNoteLength: number): RedeemResult {
  const errors: RedeemErrors = {};
  const points = parseWholePoints(form.points);
  if (points === null || points < 1) errors.points = 'redeem.error.points';
  else if (points > balance) errors.points = 'redeem.error.balance';
  const note = form.note.trim();
  if (note.length > maxNoteLength) errors.note = 'redeem.error.note';
  if (Object.keys(errors).length > 0 || points === null) return { ok: false, errors };
  return { ok: true, points, note };
}

/**
 * The money a typed number of points is worth, in cents: only the preview of the dialog while someone types. The money of a booking is
 * the one the server stored with it; null for an invalid number or while a point is worth nothing.
 */
export function redemptionCents(text: string, centsPerPoint: number): number | null {
  const points = parseWholePoints(text);
  return points === null || points < 1 || centsPerPoint <= 0 ? null : points * centsPerPoint;
}

/** The owner can undo a redemption on the day it was booked; an administrator at any time (requirements 4.12). */
export function canUndoRedemption(
  entry: { kind: string; personId: string; date: string },
  profile: { id: string; role: string } | null,
  todayKey: string,
): boolean {
  if (entry.kind !== 'redemption' || !profile) return false;
  return profile.role === 'admin' || (entry.personId === profile.id && entry.date === todayKey);
}

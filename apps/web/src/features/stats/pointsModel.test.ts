import { describe, expect, it } from 'vitest';
import type { CalendarDay } from '../../api/v2/queries.ts';
import { bonusLabel, buildRedemption, canUndoRedemption, isoWeekNumber, parseWholePoints, pointsRange, redemptionCents } from './pointsModel.ts';

const CYCLE_DAYS = 28;

/** Where `dayKey` falls, as the calendar of the server says (the cycle of the tests starts on Monday 2026-09-14). */
const day = (dayKey: string, weekStart: string, weekIndex: number, cycleIndex: number): CalendarDay => ({
  dayKey,
  weekday: 0,
  cycleIndex,
  weekIndex,
  isoWeek: '',
  weekStart,
});

describe('pointsRange', () => {
  it('covers the current week and the weeks before it, from the week start the calendar gives', () => {
    // Wednesday 16 Sep 2026.
    const wednesday = day('2026-09-16', '2026-09-14', 0, 0);
    expect(pointsRange({ unit: 'weeks', count: 1 }, wednesday, CYCLE_DAYS)).toEqual({ from: '2026-09-14', to: '2026-09-20' });
    expect(pointsRange({ unit: 'weeks', count: 3 }, wednesday, CYCLE_DAYS)).toEqual({ from: '2026-08-31', to: '2026-09-20' });
    // A Sunday still belongs to the week that started on Monday.
    expect(pointsRange({ unit: 'weeks', count: 1 }, day('2026-09-20', '2026-09-14', 0, 0), CYCLE_DAYS)).toEqual({ from: '2026-09-14', to: '2026-09-20' });
  });

  it('covers the current cycle and the cycles before it, never before the first cycle', () => {
    const cycleTwoStart = day('2026-10-12', '2026-10-12', 0, 1);
    expect(pointsRange({ unit: 'cycles', count: 1 }, cycleTwoStart, CYCLE_DAYS)).toEqual({ from: '2026-10-12', to: '2026-11-08' });
    expect(pointsRange({ unit: 'cycles', count: 2 }, cycleTwoStart, CYCLE_DAYS)).toEqual({ from: '2026-09-14', to: '2026-11-08' });
    expect(pointsRange({ unit: 'cycles', count: 13 }, cycleTwoStart, CYCLE_DAYS)).toEqual({ from: '2026-09-14', to: '2026-11-08' });
    // In the third week of a cycle the start of the cycle lies two weeks back.
    expect(pointsRange({ unit: 'cycles', count: 1 }, day('2026-10-28', '2026-10-26', 2, 1), CYCLE_DAYS)).toEqual({ from: '2026-10-12', to: '2026-11-08' });
  });

  it('shows the first cycle before the anchor, where the cycle index is negative', () => {
    // Saturday 1 Aug 2026 lies in cycle -2 (20 Jul to 16 Aug), the second week of it.
    const beforeAnchor = day('2026-08-01', '2026-07-27', 1, -2);
    expect(pointsRange({ unit: 'cycles', count: 1 }, beforeAnchor, CYCLE_DAYS)).toEqual({ from: '2026-09-14', to: '2026-10-11' });
    expect(pointsRange({ unit: 'cycles', count: 4 }, beforeAnchor, CYCLE_DAYS)).toEqual({ from: '2026-09-14', to: '2026-10-11' });
  });

  it('stays within the 371 days the entries endpoint accepts for the largest period', () => {
    // 13 cycles of 28 days: 364 days.
    const { from, to } = pointsRange({ unit: 'cycles', count: 13 }, day('2027-08-30', '2027-08-30', 2, 12), CYCLE_DAYS);
    expect(from).toBe('2026-09-14');
    expect(to).toBe('2027-09-12');
  });
});

describe('isoWeekNumber', () => {
  it('reads the week number out of the ISO week label of the calendar', () => {
    expect(isoWeekNumber('2026-W38')).toBe(38);
    expect(isoWeekNumber('2026-W01')).toBe(1);
  });

  it('is null for anything else', () => {
    expect(isoWeekNumber('')).toBeNull();
    expect(isoWeekNumber(undefined)).toBeNull();
    expect(isoWeekNumber('week 38')).toBeNull();
  });
});

describe('bonusLabel', () => {
  it('names the week of a week bonus and the days of a cycle bonus', () => {
    expect(bonusLabel({ kind: 'bonus_week_ontime', periodStart: '2026-09-28', date: '2026-10-04' }, 40)).toEqual({
      key: 'stats.points.bonus.weekOnTime',
      week: 40,
      from: '2026-09-28',
      to: '2026-10-04',
    });
    expect(bonusLabel({ kind: 'bonus_week_done', periodStart: '2026-09-28', date: '2026-10-04' }, 40)).toMatchObject({ key: 'stats.points.bonus.weekDone' });
    expect(bonusLabel({ kind: 'bonus_cycle_done', periodStart: '2026-09-07', date: '2026-10-04' }, 37)).toEqual({
      key: 'stats.points.bonus.cycleDone',
      week: 37,
      from: '2026-09-07',
      to: '2026-10-04',
    });
    expect(bonusLabel({ kind: 'bonus_cycle_ontime', periodStart: '2026-09-07', date: '2026-10-04' }, 37)).toMatchObject({ key: 'stats.points.bonus.cycleOnTime' });
  });

  it('is null for an execution, and for a bonus without a period', () => {
    expect(bonusLabel({ kind: 'execution', periodStart: null, date: '2026-09-16' }, 38)).toBeNull();
    expect(bonusLabel({ kind: 'bonus_week_done', periodStart: null, date: '2026-09-20' }, 38)).toBeNull();
  });
});

describe('parseWholePoints', () => {
  it.each([
    ['4', 4],
    [' 12 ', 12],
    ['0', 0],
    ['', null],
    ['-1', null],
    ['1.5', null],
    ['1e3', null],
    ['abc', null],
    ['1234567890', null],
  ])('%j gives %s', (text, expected) => {
    expect(parseWholePoints(text)).toBe(expected);
  });
});

describe('buildRedemption', () => {
  const NOTE_MAX = 200;

  it('accepts 1 up to the balance and trims the note', () => {
    expect(buildRedemption({ points: '1', note: '' }, 10, NOTE_MAX)).toEqual({ ok: true, points: 1, note: '' });
    expect(buildRedemption({ points: '10', note: '  Pizza  ' }, 10, NOTE_MAX)).toEqual({ ok: true, points: 10, note: 'Pizza' });
  });

  it('refuses nothing, zero, fractions and text as invalid points', () => {
    for (const points of ['', '0', '1.5', '-2', 'x']) {
      expect(buildRedemption({ points, note: '' }, 10, NOTE_MAX)).toEqual({ ok: false, errors: { points: 'redeem.error.points' } });
    }
  });

  it('refuses more than the balance, also with a balance of 0', () => {
    expect(buildRedemption({ points: '11', note: '' }, 10, NOTE_MAX)).toEqual({ ok: false, errors: { points: 'redeem.error.balance' } });
    expect(buildRedemption({ points: '1', note: '' }, 0, NOTE_MAX)).toEqual({ ok: false, errors: { points: 'redeem.error.balance' } });
  });

  it('allows a note of the length the server gives and refuses one character more, counted after trimming', () => {
    expect(buildRedemption({ points: '1', note: 'x'.repeat(200) }, 5, NOTE_MAX).ok).toBe(true);
    expect(buildRedemption({ points: '1', note: ` ${'x'.repeat(200)} ` }, 5, NOTE_MAX).ok).toBe(true);
    expect(buildRedemption({ points: '1', note: 'x'.repeat(201) }, 5, NOTE_MAX)).toEqual({ ok: false, errors: { note: 'redeem.error.note' } });
    expect(buildRedemption({ points: '1', note: 'x'.repeat(51) }, 5, 50)).toEqual({ ok: false, errors: { note: 'redeem.error.note' } });
  });

  it('reports every problem at once', () => {
    expect(buildRedemption({ points: '99', note: 'x'.repeat(201) }, 5, NOTE_MAX)).toEqual({
      ok: false,
      errors: { points: 'redeem.error.balance', note: 'redeem.error.note' },
    });
  });
});

describe('redemptionCents', () => {
  it('multiplies whole points with the cents per point, only to preview what the typed points are worth', () => {
    expect(redemptionCents('4', 25)).toBe(100);
    expect(redemptionCents(' 7 ', 10_000)).toBe(70_000);
  });

  it('is null for an invalid amount and while a point is worth nothing', () => {
    expect(redemptionCents('', 25)).toBeNull();
    expect(redemptionCents('0', 25)).toBeNull();
    expect(redemptionCents('1.5', 25)).toBeNull();
    expect(redemptionCents('4', 0)).toBeNull();
  });
});

describe('canUndoRedemption', () => {
  const entry = { kind: 'redemption', personId: 'p1', date: '2026-09-16' };
  it('lets the owner undo on the day it was booked only', () => {
    expect(canUndoRedemption(entry, { _id: 'p1', role: 'member' }, '2026-09-16')).toBe(true);
    expect(canUndoRedemption(entry, { _id: 'p1', role: 'member' }, '2026-09-17')).toBe(false);
  });

  it('refuses another member, and lets an administrator undo any time', () => {
    expect(canUndoRedemption(entry, { _id: 'p2', role: 'member' }, '2026-09-16')).toBe(false);
    expect(canUndoRedemption(entry, { _id: 'p2', role: 'planner' }, '2026-09-16')).toBe(false);
    expect(canUndoRedemption(entry, { _id: 'p2', role: 'admin' }, '2027-01-01')).toBe(true);
  });

  it('is false without a profile and for entries that are no redemption', () => {
    expect(canUndoRedemption(entry, null, '2026-09-16')).toBe(false);
    expect(canUndoRedemption({ ...entry, kind: 'execution' }, { _id: 'p2', role: 'admin' }, '2026-09-16')).toBe(false);
  });
});

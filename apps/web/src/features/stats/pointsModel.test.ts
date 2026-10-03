import { describe, expect, it } from 'vitest';
import { bonusLabel, bonusLabelOfKey, buildRedemption, canUndoRedemption, parseWholePoints, pointsRange, redemptionCents } from './pointsModel.ts';

const ANCHOR = '2026-09-14';

describe('pointsRange', () => {
  it('covers the current week and the weeks before it', () => {
    // Wednesday 16 Sep 2026.
    expect(pointsRange({ unit: 'weeks', count: 1 }, ANCHOR, '2026-09-16')).toEqual({ from: '2026-09-14', to: '2026-09-20' });
    expect(pointsRange({ unit: 'weeks', count: 3 }, ANCHOR, '2026-09-16')).toEqual({ from: '2026-08-31', to: '2026-09-20' });
    // A Sunday still belongs to the week that started on Monday.
    expect(pointsRange({ unit: 'weeks', count: 1 }, ANCHOR, '2026-09-20')).toEqual({ from: '2026-09-14', to: '2026-09-20' });
  });

  it('covers the current cycle and the cycles before it, never before the first cycle', () => {
    expect(pointsRange({ unit: 'cycles', count: 1 }, ANCHOR, '2026-10-12')).toEqual({ from: '2026-10-12', to: '2026-11-08' });
    expect(pointsRange({ unit: 'cycles', count: 2 }, ANCHOR, '2026-10-12')).toEqual({ from: '2026-09-14', to: '2026-11-08' });
    expect(pointsRange({ unit: 'cycles', count: 13 }, ANCHOR, '2026-10-12')).toEqual({ from: '2026-09-14', to: '2026-11-08' });
    // Before the anchor there is no cycle yet: the first one is shown.
    expect(pointsRange({ unit: 'cycles', count: 1 }, ANCHOR, '2026-08-01')).toEqual({ from: '2026-09-14', to: '2026-10-11' });
  });

  it('stays within the 371 days the entries endpoint accepts for the largest period', () => {
    // The 13th cycle after the anchor is the last day of a 13-cycle window: 364 days.
    const { from, to } = pointsRange({ unit: 'cycles', count: 13 }, ANCHOR, '2027-08-30');
    expect(from).toBe('2026-09-14');
    expect(to).toBe('2027-09-12');
  });
});

describe('bonusLabel', () => {
  it('names the week of a week bonus and the days of a cycle bonus', () => {
    expect(bonusLabel({ kind: 'bonus_week_ontime', periodStart: '2026-09-28', date: '2026-10-04' })).toEqual({
      key: 'stats.points.bonus.weekOnTime',
      week: 40,
      from: '2026-09-28',
      to: '2026-10-04',
    });
    expect(bonusLabel({ kind: 'bonus_week_done', periodStart: '2026-09-28', date: '2026-10-04' })).toMatchObject({ key: 'stats.points.bonus.weekDone' });
    expect(bonusLabel({ kind: 'bonus_cycle_done', periodStart: '2026-09-07', date: '2026-10-04' })).toEqual({
      key: 'stats.points.bonus.cycleDone',
      week: 37,
      from: '2026-09-07',
      to: '2026-10-04',
    });
    expect(bonusLabel({ kind: 'bonus_cycle_ontime', periodStart: '2026-09-07', date: '2026-10-04' })).toMatchObject({ key: 'stats.points.bonus.cycleOnTime' });
  });

  it('is null for an execution, and for a bonus without a period', () => {
    expect(bonusLabel({ kind: 'execution', periodStart: null, date: '2026-09-16' })).toBeNull();
    expect(bonusLabel({ kind: 'bonus_week_done', periodStart: null, date: '2026-09-20' })).toBeNull();
  });
});

describe('bonusLabelOfKey', () => {
  const person = 'a00000000000000000000001';

  it('reads the kind and the period from a ledger key, a week ending six days and a cycle 27 days after its first day', () => {
    expect(bonusLabelOfKey(`bonus_week_done:${person}:2026-09-28`)).toEqual({
      key: 'stats.points.bonus.weekDone',
      week: 40,
      from: '2026-09-28',
      to: '2026-10-04',
    });
    expect(bonusLabelOfKey(`bonus_cycle_ontime:${person}:2026-09-07`)).toEqual({
      key: 'stats.points.bonus.cycleOnTime',
      week: 37,
      from: '2026-09-07',
      to: '2026-10-04',
    });
  });

  it('is null for anything that is not a bonus key', () => {
    expect(bonusLabelOfKey(`execution:${person}`)).toBeNull();
    expect(bonusLabelOfKey('bonus_week_done:x')).toBeNull();
    expect(bonusLabelOfKey(`bonus_week_done:${person}:soon`)).toBeNull();
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
  it('accepts 1 up to the balance and trims the note', () => {
    expect(buildRedemption({ points: '1', note: '' }, 10)).toEqual({ ok: true, points: 1, note: '' });
    expect(buildRedemption({ points: '10', note: '  Pizza  ' }, 10)).toEqual({ ok: true, points: 10, note: 'Pizza' });
  });

  it('refuses nothing, zero, fractions and text as invalid points', () => {
    for (const points of ['', '0', '1.5', '-2', 'x']) {
      expect(buildRedemption({ points, note: '' }, 10)).toEqual({ ok: false, errors: { points: 'redeem.error.points' } });
    }
  });

  it('refuses more than the balance, also with a balance of 0', () => {
    expect(buildRedemption({ points: '11', note: '' }, 10)).toEqual({ ok: false, errors: { points: 'redeem.error.balance' } });
    expect(buildRedemption({ points: '1', note: '' }, 0)).toEqual({ ok: false, errors: { points: 'redeem.error.balance' } });
  });

  it('allows a note of 200 characters and refuses one of 201, counted after trimming', () => {
    expect(buildRedemption({ points: '1', note: 'x'.repeat(200) }, 5).ok).toBe(true);
    expect(buildRedemption({ points: '1', note: ` ${'x'.repeat(200)} ` }, 5).ok).toBe(true);
    expect(buildRedemption({ points: '1', note: 'x'.repeat(201) }, 5)).toEqual({ ok: false, errors: { note: 'redeem.error.note' } });
  });

  it('reports every problem at once', () => {
    expect(buildRedemption({ points: '99', note: 'x'.repeat(201) }, 5)).toEqual({
      ok: false,
      errors: { points: 'redeem.error.balance', note: 'redeem.error.note' },
    });
  });
});

describe('redemptionCents', () => {
  it('multiplies whole points with the cents per point', () => {
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

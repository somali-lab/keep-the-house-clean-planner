import { describe, expect, it } from 'vitest';
import {
  addDays,
  daysBetween,
  fromDayKey,
  isDayKey,
  isMonday,
  isoWeek,
  isoWeekLabel,
  mon0ToSun0,
  mondayOf,
  mondayOfIsoWeek,
  sun0ToMon0,
  toDayKey,
  today,
  weekdayMon0,
  weekdaySun0,
} from './time.ts';

const TZ = 'Europe/Amsterdam';

describe('day keys', () => {
  it('validates format and calendar', () => {
    expect(isDayKey('2026-09-14')).toBe(true);
    expect(isDayKey('2026-02-30')).toBe(false);
    expect(isDayKey('2026-9-14')).toBe(false);
  });

  it('today uses the timezone, not UTC', () => {
    // 22:30 UTC on 13 Sep is 00:30 on 14 Sep in Amsterdam (CEST)
    expect(today(TZ, new Date('2026-09-13T22:30:00Z'))).toBe('2026-09-14');
    expect(today('UTC', new Date('2026-09-13T22:30:00Z'))).toBe('2026-09-13');
  });

  it('round-trips local midnight', () => {
    const d = fromDayKey('2026-09-14', TZ);
    expect(d.toISOString()).toBe('2026-09-13T22:00:00.000Z');
    expect(toDayKey(d, TZ)).toBe('2026-09-14');
  });

  it('throws on invalid input', () => {
    expect(() => fromDayKey('nope', TZ)).toThrow(RangeError);
    expect(() => addDays('2026-13-01', 1)).toThrow(RangeError);
  });
});

describe('DST transitions', () => {
  it('spring forward (last Sunday of March)', () => {
    expect(fromDayKey('2026-03-29', TZ).toISOString()).toBe('2026-03-28T23:00:00.000Z');
    expect(fromDayKey('2026-03-30', TZ).toISOString()).toBe('2026-03-29T22:00:00.000Z');
    expect(addDays('2026-03-28', 1)).toBe('2026-03-29');
    expect(addDays('2026-03-28', 2)).toBe('2026-03-30');
    expect(daysBetween('2026-03-28', '2026-03-30')).toBe(2);
    // 23-hour day still maps back to the right key
    expect(toDayKey(new Date('2026-03-29T21:59:59Z'), TZ)).toBe('2026-03-29');
    expect(toDayKey(new Date('2026-03-29T22:00:00Z'), TZ)).toBe('2026-03-30');
  });

  it('fall back (last Sunday of October)', () => {
    expect(fromDayKey('2026-10-25', TZ).toISOString()).toBe('2026-10-24T22:00:00.000Z');
    expect(fromDayKey('2026-10-26', TZ).toISOString()).toBe('2026-10-25T23:00:00.000Z');
    expect(daysBetween('2026-10-24', '2026-10-27')).toBe(3);
    expect(toDayKey(new Date('2026-10-25T22:59:59Z'), TZ)).toBe('2026-10-25');
    expect(toDayKey(new Date('2026-10-25T23:00:00Z'), TZ)).toBe('2026-10-26');
  });
});

describe('weekdays', () => {
  it('converts between conventions', () => {
    // 2026-09-13 is a Sunday, 2026-09-14 a Monday
    expect(weekdaySun0('2026-09-13')).toBe(0);
    expect(weekdayMon0('2026-09-13')).toBe(6);
    expect(weekdaySun0('2026-09-14')).toBe(1);
    expect(weekdayMon0('2026-09-14')).toBe(0);
    for (let i = 0; i < 7; i++) {
      expect(mon0ToSun0(sun0ToMon0(i))).toBe(i);
    }
    expect(sun0ToMon0(0)).toBe(6);
    expect(mon0ToSun0(0)).toBe(1);
  });

  it('isMonday / mondayOf', () => {
    expect(isMonday('2026-09-14')).toBe(true);
    expect(isMonday('2026-09-13')).toBe(false);
    expect(mondayOf('2026-09-13')).toBe('2026-09-07');
    expect(mondayOf('2026-09-14')).toBe('2026-09-14');
  });
});

describe('ISO weeks', () => {
  it('labels ordinary weeks', () => {
    expect(isoWeekLabel('2026-09-14')).toBe('2026-W38');
    expect(isoWeekLabel('2026-01-05')).toBe('2026-W02');
  });

  it('handles year boundaries with week 53', () => {
    expect(isoWeek('2026-12-31')).toEqual({ year: 2026, week: 53 });
    expect(isoWeekLabel('2027-01-03')).toBe('2026-W53');
    expect(isoWeekLabel('2027-01-04')).toBe('2027-W01');
    expect(isoWeekLabel('2025-12-29')).toBe('2026-W01');
    expect(isoWeekLabel('2021-01-01')).toBe('2020-W53');
  });

  it('parses labels back to Mondays', () => {
    expect(mondayOfIsoWeek('2026-W38')).toBe('2026-09-14');
    expect(mondayOfIsoWeek('2026-W53')).toBe('2026-12-28');
    expect(mondayOfIsoWeek('2027-W53')).toBeNull();
    expect(mondayOfIsoWeek('2026-38')).toBeNull();
  });
});

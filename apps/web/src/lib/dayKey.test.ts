import { describe, expect, it } from 'vitest';
import { addDays, dayKeyInZone, daysBetween, fromDayKeyTime, mondayOfDay } from './dayKey.ts';

describe('dayKeyInZone', () => {
  it('uses the timezone, not UTC', () => {
    expect(dayKeyInZone(new Date('2026-09-15T22:30:00Z'), 'Europe/Amsterdam')).toBe('2026-09-16');
    expect(dayKeyInZone(new Date('2026-09-15T22:30:00Z'), 'UTC')).toBe('2026-09-15');
  });

  it('follows the 23-hour and 25-hour days of the DST changes', () => {
    const zone = 'Europe/Amsterdam';
    expect(dayKeyInZone(new Date('2026-03-29T21:59:59Z'), zone)).toBe('2026-03-29');
    expect(dayKeyInZone(new Date('2026-03-29T22:00:00Z'), zone)).toBe('2026-03-30');
    expect(dayKeyInZone(new Date('2026-10-25T22:59:59Z'), zone)).toBe('2026-10-25');
    expect(dayKeyInZone(new Date('2026-10-25T23:00:00Z'), zone)).toBe('2026-10-26');
  });
});

describe('addDays', () => {
  it('adds days across month, year and DST boundaries', () => {
    expect(addDays('2026-09-30', 1)).toBe('2026-10-01');
    expect(addDays('2026-10-24', 2)).toBe('2026-10-26');
    expect(addDays('2027-01-01', -1)).toBe('2026-12-31');
    expect(addDays('2026-03-28', 1)).toBe('2026-03-29');
    expect(addDays('2026-03-28', 2)).toBe('2026-03-30');
  });

  it('handles leap days', () => {
    expect(addDays('2028-02-28', 1)).toBe('2028-02-29');
    expect(addDays('2028-02-28', 2)).toBe('2028-03-01');
    expect(addDays('2026-02-28', 1)).toBe('2026-03-01');
  });

  it('throws on a value that is not a day key', () => {
    expect(() => addDays('2026-13-01', 1)).toThrow(RangeError);
    expect(() => addDays('2026-02-30', 1)).toThrow(RangeError);
    expect(() => addDays('nope', 1)).toThrow(RangeError);
  });
});

describe('mondayOfDay', () => {
  it('finds the Monday of a week', () => {
    expect(mondayOfDay('2026-09-16')).toBe('2026-09-14');
    expect(mondayOfDay('2026-09-20')).toBe('2026-09-14');
    expect(mondayOfDay('2026-09-21')).toBe('2026-09-21');
  });
});

describe('daysBetween', () => {
  it('counts whole calendar days, independent of DST', () => {
    expect(daysBetween('2026-03-28', '2026-03-30')).toBe(2);
    expect(daysBetween('2026-10-24', '2026-10-27')).toBe(3);
    expect(daysBetween('2026-09-16', '2026-09-10')).toBe(-6);
    expect(daysBetween('2026-09-16', '2026-09-16')).toBe(0);
  });
});

describe('fromDayKeyTime', () => {
  const zone = 'Europe/Amsterdam';

  it('is the instant a wall-clock time occurs on a day of the timezone', () => {
    expect(fromDayKeyTime('2026-09-16', '10:00', zone).toISOString()).toBe('2026-09-16T08:00:00.000Z');
    expect(fromDayKeyTime('2026-01-15', '10:00', zone).toISOString()).toBe('2026-01-15T09:00:00.000Z');
    expect(fromDayKeyTime('2026-09-16', '00:00', 'UTC').toISOString()).toBe('2026-09-16T00:00:00.000Z');
    expect(fromDayKeyTime('2026-09-16', '23:59', 'America/New_York').toISOString()).toBe('2026-09-17T03:59:00.000Z');
  });

  it('keeps the wall clock across the DST changes', () => {
    expect(fromDayKeyTime('2026-03-28', '12:00', zone).toISOString()).toBe('2026-03-28T11:00:00.000Z');
    expect(fromDayKeyTime('2026-03-29', '12:00', zone).toISOString()).toBe('2026-03-29T10:00:00.000Z');
    expect(fromDayKeyTime('2026-10-25', '12:00', zone).toISOString()).toBe('2026-10-25T11:00:00.000Z');
  });

  it('lands a time the day skips the same distance after the gap', () => {
    expect(fromDayKeyTime('2026-03-29', '02:30', zone).toISOString()).toBe('2026-03-29T01:30:00.000Z');
  });

  it('means the first occurrence of an ambiguous time', () => {
    expect(fromDayKeyTime('2026-10-25', '02:30', zone).toISOString()).toBe('2026-10-25T00:30:00.000Z');
  });

  it('throws on a value that is not a day key or a time of day', () => {
    expect(() => fromDayKeyTime('2026-02-30', '10:00', zone)).toThrow(RangeError);
    expect(() => fromDayKeyTime('2026-09-16', '25:00', zone)).toThrow(RangeError);
    expect(() => fromDayKeyTime('2026-09-16', '9:00', zone)).toThrow(RangeError);
  });
});

describe('fromDayKeyTime in the southern hemisphere and at the DST boundaries', () => {
  it('follows Australia/Sydney (UTC+10, UTC+11 in summer)', () => {
    expect(fromDayKeyTime('2026-07-15', '10:00', 'Australia/Sydney').toISOString()).toBe('2026-07-15T00:00:00.000Z');
    expect(fromDayKeyTime('2026-01-15', '10:00', 'Australia/Sydney').toISOString()).toBe('2026-01-14T23:00:00.000Z');
    // DST starts 2026-10-04 at 02:00 (gap 02:00-03:00) and ends 2026-04-05 at 03:00 (overlap 02:00-03:00).
    expect(fromDayKeyTime('2026-10-04', '02:30', 'Australia/Sydney').toISOString()).toBe('2026-10-03T16:30:00.000Z');
    expect(fromDayKeyTime('2026-04-05', '02:30', 'Australia/Sydney').toISOString()).toBe('2026-04-04T15:30:00.000Z');
  });

  it('follows Pacific/Auckland (UTC+12, UTC+13 in summer)', () => {
    expect(fromDayKeyTime('2026-07-15', '10:00', 'Pacific/Auckland').toISOString()).toBe('2026-07-14T22:00:00.000Z');
    expect(fromDayKeyTime('2026-01-15', '10:00', 'Pacific/Auckland').toISOString()).toBe('2026-01-14T21:00:00.000Z');
    // DST starts 2026-09-27 at 02:00 (gap) and ends 2026-04-05 at 03:00 (overlap).
    expect(fromDayKeyTime('2026-09-27', '02:30', 'Pacific/Auckland').toISOString()).toBe('2026-09-26T14:30:00.000Z');
    expect(fromDayKeyTime('2026-04-05', '02:30', 'Pacific/Auckland').toISOString()).toBe('2026-04-04T13:30:00.000Z');
  });

  it('is exact on the edges of the Amsterdam gap and overlap', () => {
    const zone = 'Europe/Amsterdam';
    expect(fromDayKeyTime('2026-03-29', '01:59', zone).toISOString()).toBe('2026-03-29T00:59:00.000Z');
    expect(fromDayKeyTime('2026-03-29', '02:00', zone).toISOString()).toBe('2026-03-29T01:00:00.000Z');
    expect(fromDayKeyTime('2026-03-29', '03:00', zone).toISOString()).toBe('2026-03-29T01:00:00.000Z');
    expect(fromDayKeyTime('2026-10-25', '01:59', zone).toISOString()).toBe('2026-10-24T23:59:00.000Z');
    expect(fromDayKeyTime('2026-10-25', '02:00', zone).toISOString()).toBe('2026-10-25T00:00:00.000Z');
    expect(fromDayKeyTime('2026-10-25', '03:00', zone).toISOString()).toBe('2026-10-25T02:00:00.000Z');
  });
});

import { describe, expect, it } from 'vitest';
import { addDays, dayKeyInZone, daysBetween } from './dayKey.ts';

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

describe('daysBetween', () => {
  it('counts whole calendar days, independent of DST', () => {
    expect(daysBetween('2026-03-28', '2026-03-30')).toBe(2);
    expect(daysBetween('2026-10-24', '2026-10-27')).toBe(3);
    expect(daysBetween('2026-09-16', '2026-09-10')).toBe(-6);
    expect(daysBetween('2026-09-16', '2026-09-16')).toBe(0);
  });
});

import { describe, expect, it } from 'vitest';
import {
  assertValidAnchor,
  cycleEnd,
  cycleIndexFor,
  cycleStart,
  slotDate,
  weekIndexFor,
} from './cycle.ts';
import { fromDayKey, toDayKey, weekdaySun0 } from './time.ts';

const ANCHOR = '2026-09-14'; // Monday

describe('anchor validation', () => {
  it('accepts Mondays only', () => {
    expect(() => assertValidAnchor(ANCHOR)).not.toThrow();
    expect(() => assertValidAnchor('2026-09-13')).toThrow(RangeError);
    expect(() => assertValidAnchor('not-a-date')).toThrow(RangeError);
    expect(() => cycleIndexFor('2026-09-20', '2026-09-15')).toThrow(RangeError);
  });
});

describe('cycle index', () => {
  it('computes index for days around the anchor', () => {
    expect(cycleIndexFor(ANCHOR, ANCHOR)).toBe(0);
    expect(cycleIndexFor('2026-10-11', ANCHOR)).toBe(0);
    expect(cycleIndexFor('2026-10-12', ANCHOR)).toBe(1);
    expect(cycleIndexFor('2026-09-13', ANCHOR)).toBe(-1);
    expect(cycleIndexFor('2026-08-17', ANCHOR)).toBe(-1);
    expect(cycleIndexFor('2026-08-16', ANCHOR)).toBe(-2);
  });

  it('computes start and end', () => {
    expect(cycleStart(0, ANCHOR)).toBe(ANCHOR);
    expect(cycleStart(1, ANCHOR)).toBe('2026-10-12');
    expect(cycleStart(-1, ANCHOR)).toBe('2026-08-17');
    expect(cycleEnd(0, ANCHOR)).toBe('2026-10-11');
  });

  it('week index within the cycle, including negative cycles', () => {
    expect(weekIndexFor(ANCHOR, ANCHOR)).toBe(0);
    expect(weekIndexFor('2026-09-20', ANCHOR)).toBe(0);
    expect(weekIndexFor('2026-09-21', ANCHOR)).toBe(1);
    expect(weekIndexFor('2026-10-11', ANCHOR)).toBe(3);
    expect(weekIndexFor('2026-09-13', ANCHOR)).toBe(3);
  });

  it('crosses the October DST transition without drift', () => {
    // cycle 1 contains 25 Oct 2026 (fall back)
    const start = cycleStart(1, ANCHOR);
    const dates = [0, 1, 2, 3].map((w) => slotDate(start, w, 0));
    expect(dates).toEqual(['2026-10-18', '2026-10-25', '2026-11-01', '2026-11-08']);
    for (const d of dates) {
      expect(toDayKey(fromDayKey(d))).toBe(d);
      expect(cycleIndexFor(d, ANCHOR)).toBe(1);
    }
  });
});

describe('slotDate', () => {
  it('maps all 28 weekIndex × weekday combinations to distinct dates in the cycle', () => {
    const start = cycleStart(2, ANCHOR);
    const seen = new Set<string>();
    for (let weekIndex = 0; weekIndex < 4; weekIndex++) {
      for (let weekday = 0; weekday < 7; weekday++) {
        const date = slotDate(start, weekIndex, weekday);
        expect(weekdaySun0(date)).toBe(weekday);
        expect(weekIndexFor(date, ANCHOR)).toBe(weekIndex);
        expect(cycleIndexFor(date, ANCHOR)).toBe(2);
        seen.add(date);
      }
    }
    expect(seen.size).toBe(28);
  });

  it('puts Sunday at the end of the Monday-first week', () => {
    expect(slotDate(ANCHOR, 0, 1)).toBe('2026-09-14');
    expect(slotDate(ANCHOR, 0, 0)).toBe('2026-09-20');
    expect(slotDate(ANCHOR, 3, 6)).toBe('2026-10-10');
  });

  it('rejects out-of-range values', () => {
    expect(() => slotDate(ANCHOR, 4, 0)).toThrow(RangeError);
    expect(() => slotDate(ANCHOR, 0, 7)).toThrow(RangeError);
    expect(() => slotDate(ANCHOR, -1, 0)).toThrow(RangeError);
  });
});

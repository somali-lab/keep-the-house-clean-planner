import { describe, expect, it } from 'vitest';
import {
  periodRolledOver,
  resetCelebrationMemory,
  celebrationKey,
  celebrationMode,
  chickenOffset,
  goalReached,
  markCelebrated,
  wasCelebrated,
  type CelebrationStorage,
} from './rewardModel.ts';

/** A storage that lives in memory and can be made to fail, like a browser with storage switched off. */
function memoryStorage(failing = false): CelebrationStorage & { data: Map<string, string> } {
  const data = new Map<string, string>();
  return {
    data,
    getItem: (key) => {
      if (failing) throw new Error('storage disabled');
      return data.get(key) ?? null;
    },
    setItem: (key, value) => {
      if (failing) throw new Error('storage disabled');
      data.set(key, value);
    },
  };
}

describe('goalReached', () => {
  it('is true only with a goal and 100%', () => {
    expect(goalReached({ goalPoints: 4, percent: 100 })).toBe(true);
    expect(goalReached({ goalPoints: 4, percent: 99 })).toBe(false);
    expect(goalReached({ goalPoints: null, percent: 100 })).toBe(false);
  });
});

describe('chickenOffset', () => {
  it('keeps the chicken on the track', () => {
    expect([-5, 0, 42, 100, 140, Number.NaN].map(chickenOffset)).toEqual([0, 0, 42, 100, 100, 0]);
  });
});

describe('periodRolledOver', () => {
  it('is true only when the day lies outside the period that was read', () => {
    const week = { start: '2026-09-14', end: '2026-09-20' };
    expect(['2026-09-14', '2026-09-17', '2026-09-20'].map((day) => periodRolledOver(week, day))).toEqual([false, false, false]);
    expect(['2026-09-13', '2026-09-21'].map((day) => periodRolledOver(week, day))).toEqual([true, true]);
  });
});

describe('celebrationKey', () => {
  it('names the person, the period and its first day, so every period celebrates once', () => {
    expect(celebrationKey('a00000000000000000000001', 'week', '2026-09-14')).toBe('khc.rewardCelebrated.a00000000000000000000001.week.2026-09-14');
    expect(celebrationKey('a00000000000000000000001', 'cycle', '2026-09-14')).not.toBe(celebrationKey('a00000000000000000000001', 'week', '2026-09-14'));
    expect(celebrationKey('a00000000000000000000001', 'week', '2026-09-21')).not.toBe(celebrationKey('a00000000000000000000001', 'week', '2026-09-14'));
    expect(celebrationKey('b00000000000000000000002', 'week', '2026-09-14')).not.toBe(celebrationKey('a00000000000000000000001', 'week', '2026-09-14'));
  });
});

describe('the animation plays once per person and period', () => {
  it('plays the first time the full meter is seen, remembers it, and never plays again in that period', () => {
    const storage = memoryStorage();
    const key = celebrationKey('a00000000000000000000001', 'week', '2026-09-14');
    const first = celebrationMode({ reached: true, alreadyCelebrated: wasCelebrated(key, storage), reducedMotion: false });
    expect(first).toBe('animate');
    markCelebrated(key, storage);
    expect(storage.data.get(key)).toBe('1');
    expect(celebrationMode({ reached: true, alreadyCelebrated: wasCelebrated(key, storage), reducedMotion: false })).toBe('static');

    // The next week is a new period with a new key: it plays again there, for this person and for another one.
    const nextWeek = celebrationKey('a00000000000000000000001', 'week', '2026-09-21');
    expect(celebrationMode({ reached: true, alreadyCelebrated: wasCelebrated(nextWeek, storage), reducedMotion: false })).toBe('animate');
    const other = celebrationKey('b00000000000000000000002', 'week', '2026-09-14');
    expect(wasCelebrated(other, storage)).toBe(false);
  });

  it('shows nothing while the goal is not met, and only the static message with reduced motion', () => {
    expect(celebrationMode({ reached: false, alreadyCelebrated: false, reducedMotion: false })).toBe('none');
    expect(celebrationMode({ reached: false, alreadyCelebrated: true, reducedMotion: true })).toBe('none');
    expect(celebrationMode({ reached: true, alreadyCelebrated: false, reducedMotion: true })).toBe('static');
  });

  it('copes with storage that is missing or throws: never an error, and remembered in memory for this page', () => {
    const failing = memoryStorage(true);
    expect(wasCelebrated('khc.rewardCelebrated.x', failing)).toBe(false);
    expect(() => markCelebrated('khc.rewardCelebrated.x', failing)).not.toThrow();
    // Nothing reached storage, but a remount in this page still knows it played.
    expect(wasCelebrated('khc.rewardCelebrated.x', failing)).toBe(true);
    expect(wasCelebrated('khc.rewardCelebrated.x', null)).toBe(true);
    expect(wasCelebrated('khc.rewardCelebrated.y', null)).toBe(false);
    expect(() => markCelebrated('khc.rewardCelebrated.y', null)).not.toThrow();
    expect(wasCelebrated('khc.rewardCelebrated.y', null)).toBe(true);
    resetCelebrationMemory();
    expect(wasCelebrated('khc.rewardCelebrated.x', null)).toBe(false);
  });

  it('reads and writes the real localStorage by default', () => {
    const key = celebrationKey('a00000000000000000000001', 'cycle', '2026-09-14');
    expect(wasCelebrated(key)).toBe(false);
    markCelebrated(key);
    expect(window.localStorage.getItem(key)).toBe('1');
    expect(wasCelebrated(key)).toBe(true);
  });
});

import type { RewardPeriod, RewardProgress } from './api.ts';

export type { RewardPeriod };
export const REWARD_PERIODS: RewardPeriod[] = ['week', 'cycle'];

/** The meter has a goal and it is met. A period without a goal never counts as reached. */
export function goalReached(progress: Pick<RewardProgress, 'goalPoints' | 'percent'>): boolean {
  return progress.goalPoints !== null && progress.percent >= 100;
}

/** Where the chicken stands on the track, as a share of the track from 0 to 100. */
export function chickenOffset(percent: number): number {
  return Number.isFinite(percent) ? Math.min(100, Math.max(0, percent)) : 0;
}

/** The localStorage key that remembers the completion animation of one person for one period (requirements 4.12). */
export function celebrationKey(personId: string, period: RewardPeriod, startDayKey: string): string {
  return `khc.rewardCelebrated.${personId}.${period}.${startDayKey}`;
}

/** The small part of `localStorage` the celebration needs, so tests can inject a fake. */
export interface CelebrationStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

function browserStorage(): CelebrationStorage | null {
  try {
    return window.localStorage;
  } catch {
    return null;
  }
}

/**
 * The keys that played in this page, kept in memory as well: when storage is blocked or throws, a remount (the
 * week/cycle toggle, another tab and back) still never plays the animation again; only a reload can.
 */
const playedInMemory = new Set<string>();

/** Forgets what played in this page. Tests use it; the app never needs to. */
export function resetCelebrationMemory(): void {
  playedInMemory.clear();
}

/** Whether the animation of this period already played, here or (from storage) before. Unreadable storage is not an error. */
export function wasCelebrated(key: string, storage: CelebrationStorage | null = browserStorage()): boolean {
  if (playedInMemory.has(key)) return true;
  try {
    return storage?.getItem(key) != null;
  } catch {
    return false;
  }
}

/** Remembers that the animation played, in memory and in storage; without usable storage a reload may play it again. */
export function markCelebrated(key: string, storage: CelebrationStorage | null = browserStorage()): void {
  playedInMemory.add(key);
  try {
    storage?.setItem(key, '1');
  } catch {
    // The memory above still keeps it from playing twice while this page stays open.
  }
}

/** True when the day is outside the period the progress was read for: the week or cycle rolled over and the data is stale. */
export function periodRolledOver(progress: Pick<RewardProgress, 'start' | 'end'>, todayKey: string): boolean {
  return todayKey < progress.start || todayKey > progress.end;
}

/**
 * What to show when the meter is full (requirements 4.12): `animate` plays the completion animation, once, the first time a
 * person sees the full meter in a period; `static` is the text "Doel gehaald!" without any motion, for people who
 * asked for reduced motion and after the animation played; `none` while the goal is not met.
 */
export function celebrationMode(options: {
  reached: boolean;
  alreadyCelebrated: boolean;
  reducedMotion: boolean;
}): 'animate' | 'static' | 'none' {
  if (!options.reached) return 'none';
  return options.reducedMotion || options.alreadyCelebrated ? 'static' : 'animate';
}

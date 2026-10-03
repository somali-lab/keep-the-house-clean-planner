import type { PointsProgressResponse, RewardPeriod } from '@huishoudplanner/shared';
import { eggsForPercent, REWARD_EGG_COUNT } from '@huishoudplanner/shared/rewards';

export type { RewardPeriod };
export const REWARD_PERIODS: RewardPeriod[] = ['week', 'cycle'];

/** Number of eggs of the meter and how many are in the basket for a percentage: one per full 10% (ADR-0015). */
export const EGG_COUNT = REWARD_EGG_COUNT;
export const eggsInBasket = eggsForPercent;

/** The meter has a goal and it is met. A period without a goal never counts as reached. */
export function goalReached(progress: Pick<PointsProgressResponse, 'goalPoints' | 'percent'>): boolean {
  return progress.goalPoints !== null && progress.percent >= 100;
}

/** Where the chicken stands on the track, as a share of the track from 0 to 100. */
export function chickenOffset(percent: number): number {
  return Number.isFinite(percent) ? Math.min(100, Math.max(0, percent)) : 0;
}

/** The localStorage key that remembers the completion animation of one person for one period (ADR-0015). */
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

/** Whether the animation of this period already played. Unreadable storage counts as not played. */
export function wasCelebrated(key: string, storage: CelebrationStorage | null = browserStorage()): boolean {
  try {
    return storage?.getItem(key) != null;
  } catch {
    return false;
  }
}

/** Remembers that the animation played. Without usable storage it stays in memory only, so a reload may play it again. */
export function markCelebrated(key: string, storage: CelebrationStorage | null = browserStorage()): void {
  try {
    storage?.setItem(key, '1');
  } catch {
    // The animation still plays only once while this page stays open.
  }
}

/**
 * What to show when the meter is full (ADR-0015): `animate` plays the completion animation, once, the first time a
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

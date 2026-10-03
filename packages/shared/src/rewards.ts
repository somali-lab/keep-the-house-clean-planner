import { periodDayOf, periodOwnerOf, type BonusOccurrence, type Period } from './bonuses.ts';

/**
 * The reward meter (ADR-0015): pure rules over day keys and string ids. A period is the calendar week or the
 * cycle of `bonuses.ts`; the earned points come from the ledger and the goal is either set by an administrator
 * or the points of the work planned for the person.
 */

/** Smallest and largest explicit goal of a week or a cycle, in points. 0 means no goal. */
export const MIN_REWARD_GOAL_POINTS = 0;
export const MAX_REWARD_GOAL_POINTS = 100_000;

/** Eggs in the basket when the meter is full: one egg per 10%. */
export const REWARD_EGG_COUNT = 10;

/** The goals an administrator can set; `null` means the goal is automatic (ADR-0015). */
export interface RewardGoals {
  weekPoints: number | null;
  cyclePoints: number | null;
}

export const NO_REWARD_GOALS: RewardGoals = { weekPoints: null, cyclePoints: null };

export function sameRewardGoals(a: RewardGoals, b: RewardGoals): boolean {
  return a.weekPoints === b.weekPoints && a.cyclePoints === b.cyclePoints;
}

/** A planned occurrence with the points it is worth: its snapshot when it is done, else what the task is worth now. */
export interface GoalOccurrence extends BonusOccurrence {
  points: number;
}

export interface AutomaticGoal {
  /** Planned occurrences of the person as owner in the period; skipped work and recorded work do not count. */
  planned: number;
  /** The sum of their points. */
  points: number;
}

/**
 * The automatic goal of a person (ADR-0015): the points of the work planned for them in the period. The owner
 * is the one of the bonuses (`periodOwnerId`, else the assignee) and the day that places an occurrence in a
 * period is its planned day, so overdue work dragged to today still belongs to the week it was planned in and
 * work somebody else did stays in the owner's goal. Recorded extra work was never planned and is left out;
 * skipped work cannot be earned and is left out too.
 */
export function automaticGoal(items: readonly GoalOccurrence[], personId: string, period: Pick<Period, 'start' | 'end'>): AutomaticGoal {
  let planned = 0;
  let points = 0;
  for (const item of items) {
    if (item.recordedDone === true || item.status === 'skipped') continue;
    if (periodOwnerOf(item) !== personId) continue;
    const day = periodDayOf(item);
    if (day < period.start || day > period.end) continue;
    planned += 1;
    points += item.points;
  }
  return { planned, points };
}

export type RewardGoalSource = 'explicit' | 'automatic';

export interface ResolvedRewardGoal {
  /** Null when there is no goal: nothing planned for the person, or an explicit 0. */
  goalPoints: number | null;
  source: RewardGoalSource;
}

/**
 * The goal of a period: the explicit goal when an administrator set one (0 switches the goal off), else the
 * automatic goal, at least 1, and none at all when nothing is planned for the person.
 */
export function resolveRewardGoal(explicit: number | null | undefined, automatic: AutomaticGoal): ResolvedRewardGoal {
  if (explicit !== null && explicit !== undefined) return { goalPoints: explicit > 0 ? explicit : null, source: 'explicit' };
  return { goalPoints: automatic.planned > 0 ? Math.max(1, automatic.points) : null, source: 'automatic' };
}

/** The share of the goal that is earned, a whole number from 0 to 100; it only reaches 100 when the goal is met. */
export function rewardPercent(earnedPoints: number, goalPoints: number | null): number {
  if (goalPoints === null || goalPoints <= 0 || earnedPoints <= 0) return 0;
  return Math.min(100, Math.floor((earnedPoints * 100) / goalPoints));
}

/** Eggs in the basket for a percentage: one per full 10%, 0 to {@link REWARD_EGG_COUNT}. */
export function eggsForPercent(percent: number): number {
  if (!Number.isFinite(percent)) return 0;
  return Math.min(REWARD_EGG_COUNT, Math.max(0, Math.floor(percent / 10)));
}

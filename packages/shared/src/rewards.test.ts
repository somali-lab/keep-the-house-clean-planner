import { describe, expect, it } from 'vitest';
import { weekOf } from './bonuses.ts';
import {
  automaticGoal,
  eggsForPercent,
  MAX_REWARD_GOAL_POINTS,
  NO_REWARD_GOALS,
  resolveRewardGoal,
  REWARD_EGG_COUNT,
  rewardPercent,
  sameRewardGoals,
  type GoalOccurrence,
} from './rewards.ts';
import { pointsProgressQuerySchema, pointsProgressResponseSchema } from './schemas/points.ts';
import { settingsSchema, updateSettingsInputSchema } from './schemas/settings.ts';

const WEEK = weekOf('2026-09-16'); // 14 to 20 September 2026

function occurrence(overrides: Partial<GoalOccurrence> = {}): GoalOccurrence {
  return {
    status: 'open',
    plannedDate: '2026-09-15',
    date: '2026-09-15',
    assigneeId: 'anna',
    completedBy: null,
    completedAt: null,
    points: 3,
    ...overrides,
  };
}

describe('automaticGoal', () => {
  it('sums the points of the work planned for the person in the period, and counts it', () => {
    const items = [
      occurrence({ points: 3 }),
      occurrence({ plannedDate: '2026-09-14', date: '2026-09-14', points: 2 }),
      occurrence({ plannedDate: '2026-09-20', date: '2026-09-20', points: 1, status: 'done', completedBy: 'anna', completedAt: '2026-09-20T10:00:00.000Z' }),
    ];
    expect(automaticGoal(items, 'anna', WEEK)).toEqual({ planned: 3, points: 6 });
  });

  it('puts work in a period by the day it was planned, not the day it sits on now, and includes both ends', () => {
    const items = [
      // Overdue work dragged to today still belongs to the week it was planned in.
      occurrence({ plannedDate: '2026-09-10', date: '2026-09-16', points: 5 }),
      // Planned for next week and moved forward into this one: it belongs to next week.
      occurrence({ plannedDate: '2026-09-21', date: '2026-09-18', points: 7 }),
      occurrence({ plannedDate: '2026-09-14', date: '2026-09-14', points: 1 }),
      occurrence({ plannedDate: '2026-09-20', date: '2026-09-20', points: 2 }),
    ];
    expect(automaticGoal(items, 'anna', WEEK)).toEqual({ planned: 2, points: 3 });
  });

  it('leaves out skipped work, recorded work and work of other people or nobody', () => {
    const items = [
      occurrence({ status: 'skipped', points: 4 }),
      occurrence({ recordedDone: true, status: 'done', completedBy: 'anna', points: 4 }),
      occurrence({ assigneeId: 'bram', points: 4 }),
      occurrence({ assigneeId: null, points: 4 }),
      occurrence({ points: 1 }),
    ];
    expect(automaticGoal(items, 'anna', WEEK)).toEqual({ planned: 1, points: 1 });
  });

  it('follows the period owner: the frozen owner keeps the work when somebody else did it, and a missing freeze means the assignee', () => {
    const items = [
      // Bram finished it after the week ended; Anna, who it was planned for, still has it in her goal.
      occurrence({ assigneeId: 'bram', periodOwnerId: 'anna', status: 'done', completedBy: 'bram', points: 3 }),
      // Anna's work that Bram took over inside the week is Bram's; no frozen owner, so it follows the assignee.
      occurrence({ assigneeId: 'bram', points: 2 }),
      // Unassigned at the time the week ended, then claimed: nobody's.
      occurrence({ assigneeId: 'anna', periodOwnerId: null, points: 8 }),
    ];
    expect(automaticGoal(items, 'anna', WEEK)).toEqual({ planned: 1, points: 3 });
    expect(automaticGoal(items, 'bram', WEEK)).toEqual({ planned: 1, points: 2 });
  });

  it('is empty without work, and counts planned work that is worth 0 points', () => {
    expect(automaticGoal([], 'anna', WEEK)).toEqual({ planned: 0, points: 0 });
    expect(automaticGoal([occurrence({ points: 0 })], 'anna', WEEK)).toEqual({ planned: 1, points: 0 });
  });
});

describe('resolveRewardGoal', () => {
  it('takes an explicit goal over the planned work', () => {
    expect(resolveRewardGoal(12, { planned: 2, points: 6 })).toEqual({ goalPoints: 12, source: 'explicit' });
    expect(resolveRewardGoal(12, { planned: 0, points: 0 })).toEqual({ goalPoints: 12, source: 'explicit' });
  });

  it('treats an explicit 0 as no goal', () => {
    expect(resolveRewardGoal(0, { planned: 2, points: 6 })).toEqual({ goalPoints: null, source: 'explicit' });
  });

  it('uses the planned points as the automatic goal, at least 1, and none when nothing is planned', () => {
    expect(resolveRewardGoal(null, { planned: 2, points: 6 })).toEqual({ goalPoints: 6, source: 'automatic' });
    expect(resolveRewardGoal(undefined, { planned: 1, points: 0 })).toEqual({ goalPoints: 1, source: 'automatic' });
    expect(resolveRewardGoal(null, { planned: 0, points: 0 })).toEqual({ goalPoints: null, source: 'automatic' });
  });
});

describe('rewardPercent', () => {
  it.each([
    [0, 10, 0],
    [1, 10, 10],
    [3, 4, 75],
    [1, 3, 33],
    [2, 3, 66],
    [9, 10, 90],
    [99, 100, 99],
    [10, 10, 100],
    [25, 10, 100],
    [-5, 10, 0],
  ])('%i of %i points is %i%%', (earned, goal, expected) => {
    expect(rewardPercent(earned, goal)).toBe(expected);
  });

  it('only reaches 100 when the goal is met, and is 0 without a goal', () => {
    expect(rewardPercent(999, 1000)).toBe(99);
    expect(rewardPercent(5, null)).toBe(0);
    expect(rewardPercent(5, 0)).toBe(0);
  });
});

describe('eggsForPercent', () => {
  it('gives one egg for every full 10%, from 0 to 10', () => {
    expect(REWARD_EGG_COUNT).toBe(10);
    expect([0, 9, 10, 19, 20, 55, 90, 99, 100].map(eggsForPercent)).toEqual([0, 0, 1, 1, 2, 5, 9, 9, 10]);
  });

  it('clamps values that are out of range or not numbers', () => {
    expect(eggsForPercent(-20)).toBe(0);
    expect(eggsForPercent(250)).toBe(10);
    expect(eggsForPercent(Number.NaN)).toBe(0);
  });
});

describe('goals', () => {
  it('compares both goals', () => {
    expect(sameRewardGoals(NO_REWARD_GOALS, { weekPoints: null, cyclePoints: null })).toBe(true);
    expect(sameRewardGoals(NO_REWARD_GOALS, { weekPoints: 0, cyclePoints: null })).toBe(false);
    expect(sameRewardGoals({ weekPoints: 1, cyclePoints: 2 }, { weekPoints: 1, cyclePoints: 3 })).toBe(false);
  });
});

describe('reward schemas', () => {
  it('accepts goals from 0 to 100000 and null, and nothing else', () => {
    const parse = (rewardGoals: unknown) => updateSettingsInputSchema.safeParse({ rewardGoals }).success;
    expect(parse({ weekPoints: 0, cyclePoints: MAX_REWARD_GOAL_POINTS })).toBe(true);
    expect(parse({ weekPoints: null, cyclePoints: null })).toBe(true);
    expect(parse({ weekPoints: -1, cyclePoints: null })).toBe(false);
    expect(parse({ weekPoints: null, cyclePoints: MAX_REWARD_GOAL_POINTS + 1 })).toBe(false);
    expect(parse({ weekPoints: 1.5, cyclePoints: null })).toBe(false);
    expect(parse({ weekPoints: null })).toBe(false);
    expect(updateSettingsInputSchema.safeParse({}).success).toBe(true);
  });

  it('keeps the goals optional in stored settings, so older exports still import', () => {
    const base = {
      cycleAnchorDate: '2026-09-14',
      weekStartsOn: 1,
      timezone: 'Europe/Amsterdam',
      vacationRanges: [],
      intervals: [],
      aiProvider: { type: 'none' },
      promoteThreshold: 2,
      dismissedPromotions: [],
      createdAt: '2026-09-14T08:00:00.000Z',
      updatedAt: '2026-09-14T08:00:00.000Z',
    };
    expect(settingsSchema.safeParse(base).success).toBe(true);
    expect(settingsSchema.safeParse({ ...base, rewardGoals: { weekPoints: 5, cyclePoints: null } }).success).toBe(true);
  });

  it('needs a person and a known period for the progress read', () => {
    const personId = 'a'.repeat(24);
    expect(pointsProgressQuerySchema.safeParse({ personId, period: 'week' }).success).toBe(true);
    expect(pointsProgressQuerySchema.safeParse({ personId, period: 'cycle' }).success).toBe(true);
    expect(pointsProgressQuerySchema.safeParse({ personId, period: 'month' }).success).toBe(false);
    expect(pointsProgressQuerySchema.safeParse({ period: 'week' }).success).toBe(false);
  });

  it('describes the answer of the progress read, with or without a goal and money', () => {
    const answer = {
      personId: 'a'.repeat(24),
      period: 'week',
      start: '2026-09-14',
      end: '2026-09-20',
      earnedPoints: 3,
      goalPoints: 4,
      goalSource: 'automatic',
      percent: 75,
      currencyCode: 'EUR',
      centsPerPoint: 25,
      money: { earned: 75, goal: 100 },
    };
    expect(pointsProgressResponseSchema.safeParse(answer).success).toBe(true);
    expect(pointsProgressResponseSchema.safeParse({ ...answer, goalPoints: null, percent: 0, money: null, centsPerPoint: 0 }).success).toBe(true);
    expect(pointsProgressResponseSchema.safeParse({ ...answer, percent: 101 }).success).toBe(false);
  });
});

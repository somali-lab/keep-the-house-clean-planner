import { describe, expect, it } from 'vitest';
import { DEFAULT_INTERVALS } from '../schemas/intervals.ts';
import {
  budgetFor,
  validatePlan,
  type PlanSlot,
  type PlanTask,
  type PlanUser,
  type ValidatePlanInput,
} from './plan.ts';

const ANNA = 'a00000000000000000000001';
const BRAM = 'b00000000000000000000002';
const GONE = 'c00000000000000000000003';

const users: PlanUser[] = [
  { _id: ANNA, name: 'Anna', active: true, unavailableWeekdays: [2], dailyBudgetMinutes: { weekday: 60, weekend: 120 }, maxDailyMinutes: { weekday: 480, weekend: 480 } },
  { _id: BRAM, name: 'Bram', active: true, unavailableWeekdays: [], dailyBudgetMinutes: { weekday: 60, weekend: 120 }, maxDailyMinutes: { weekday: 480, weekend: 480 } },
  { _id: GONE, name: 'Oud', active: false, unavailableWeekdays: [], dailyBudgetMinutes: { weekday: 60, weekend: 120 }, maxDailyMinutes: { weekday: 480, weekend: 480 } },
];

const task = (id: string, intervalKey: string, durationMinutes: number, active = true): PlanTask => ({
  _id: id,
  name: id,
  intervalKey,
  durationMinutes,
  active,
});

const WEEKLY = task('weekly', '1w', 40);
const TWICE = task('twice', '2w', 10);
const QUARTER = task('quarter', 'quarter', 90);
const MONTHLY = task('monthly', '4wk', 40);
const OLD = task('old', '1w', 10, false);
const tasks = [WEEKLY, TWICE, QUARTER, MONTHLY, OLD];

const slot = (taskId: string, weekIndex: number, weekday: number, assigneeId: string | null = BRAM): PlanSlot => ({
  taskId,
  weekIndex,
  weekday,
  assigneeId,
});

/** Slots that satisfy every interval exactly with no budget issues. */
const fullPlan = (): PlanSlot[] => [
  ...[0, 1, 2, 3].map((w) => slot('weekly', w, 1)),
  ...[0, 1, 2, 3].flatMap((w) => [slot('twice', w, 3), slot('twice', w, 6)]),
  slot('monthly', 0, 5, ANNA),
];

const run = (slots: PlanSlot[], overrides: Partial<ValidatePlanInput> = {}) =>
  validatePlan({ slots, tasks, users, intervals: DEFAULT_INTERVALS, ...overrides });

const codes = (issues: { code: string }[]) => issues.map((i) => i.code);

describe('validatePlan — errors', () => {
  it('accepts a plan that satisfies every rule', () => {
    const result = run(fullPlan());
    expect(result.errors).toEqual([]);
    expect(result.warnings).toEqual([]);
  });

  it.each<[string, PlanSlot, string]>([
    ['assignee unavailable on that weekday', slot('weekly', 0, 2, ANNA), 'assignee_unavailable'],
    ['unknown task', slot('nope', 0, 1), 'unknown_task'],
    ['inactive task', slot('old', 0, 1), 'inactive_task'],
    ['unknown user', slot('weekly', 0, 4, 'd00000000000000000000004'), 'unknown_user'],
    ['inactive user', slot('weekly', 0, 4, GONE), 'inactive_user'],
    ['weekIndex above range', slot('weekly', 4, 1), 'week_index_out_of_range'],
    ['negative weekIndex', slot('weekly', -1, 1), 'week_index_out_of_range'],
    ['fractional weekIndex', slot('weekly', 1.5, 1), 'week_index_out_of_range'],
    ['weekday above range', slot('weekly', 0, 7), 'weekday_out_of_range'],
  ])('%s → %s', (_label, bad, code) => {
    const slots = [...fullPlan(), bad];
    const result = run(slots);
    expect(codes(result.errors)).toEqual([code]);
    expect(result.errors[0]).toMatchObject({ slotIndex: slots.length - 1, taskId: bad.taskId });
  });

  it('allows the same unavailable weekday for another user', () => {
    expect(run([...fullPlan(), slot('quarter', 0, 2, BRAM)]).errors).toEqual([]);
  });

  it('allows an unassigned slot on any weekday', () => {
    expect(run([...fullPlan(), slot('quarter', 0, 2, null)]).errors).toEqual([]);
  });

  it('rejects the same task twice on the same day, even for different assignees', () => {
    const result = run([...fullPlan(), slot('weekly', 0, 1, ANNA)]);
    expect(codes(result.errors)).toEqual(['duplicate_task_day']);
  });

  it('allows the same task on the same weekday in different weeks', () => {
    expect(run([slot('quarter', 0, 1), slot('quarter', 1, 1)]).errors).toEqual([]);
  });
});

describe('validatePlan — interval warnings', () => {
  it('2w with 5 slots is a warning, not an error', () => {
    const slots = [...fullPlan().filter((s) => s.taskId !== 'twice'), ...[0, 1, 2, 3, 4].map((i) => slot('twice', i % 4, i < 4 ? 3 : 4))];
    const result = run(slots);
    expect(result.errors).toEqual([]);
    expect(result.warnings).toEqual([{ code: 'interval_mismatch', taskId: 'twice', placed: 5, required: 8 }]);
  });

  it('warns for active tasks that are not placed at all', () => {
    const result = run(fullPlan().filter((s) => s.taskId !== 'monthly'));
    expect(result.warnings).toEqual([{ code: 'interval_mismatch', taskId: 'monthly', placed: 0, required: 1 }]);
  });

  it('skips the check when perCycle is null', () => {
    const none = run([...fullPlan()]);
    // unassigned, so the 90-minute task does not trip a budget warning
    const some = run([...fullPlan(), slot('quarter', 2, 4, null)]);
    expect(none.warnings).toEqual([]);
    expect(some.warnings).toEqual([]);
    expect(some.summary.tasks.find((t) => t.taskId === 'quarter')).toEqual({ taskId: 'quarter', placed: 1, required: null });
  });

  it('reports placed/required per task, leaving out inactive tasks without slots', () => {
    const summary = run(fullPlan()).summary.tasks;
    expect(summary).toEqual([
      { taskId: 'weekly', placed: 4, required: 4 },
      { taskId: 'twice', placed: 8, required: 8 },
      { taskId: 'quarter', placed: 0, required: null },
      { taskId: 'monthly', placed: 1, required: 1 },
    ]);
  });

  it('treats a task with an unknown interval key as not grid-planned', () => {
    const result = run([slot('odd', 0, 1)], { tasks: [task('odd', 'mystery', 5)] });
    expect(result.warnings).toEqual([]);
    expect(result.summary.tasks).toEqual([{ taskId: 'odd', placed: 1, required: null }]);
  });
});

describe('validatePlan — budgets and totals', () => {
  it('uses the weekday budget Monday–Friday', () => {
    // Tuesday (2), Bram: 40 + 40 = 80 > 60
    const result = run([slot('weekly', 1, 2), slot('monthly', 1, 2)], { tasks: [WEEKLY, MONTHLY] });
    expect(result.warnings.filter((w) => w.code === 'over_budget')).toEqual([
      { code: 'over_budget', userId: BRAM, weekIndex: 1, period: 'weekday', minutes: 80, budget: 60 },
    ]);
  });

  it('uses the weekend budget on Saturday and Sunday', () => {
    const sat = run([slot('weekly', 0, 6), slot('monthly', 0, 6)], { tasks: [WEEKLY, MONTHLY] });
    expect(sat.warnings.filter((w) => w.code === 'over_budget')).toEqual([]);
    const sun = run([slot('weekly', 0, 0), slot('monthly', 0, 0), slot('quarter', 0, 0)], {
      tasks: [WEEKLY, MONTHLY, QUARTER],
    });
    expect(sun.warnings.filter((w) => w.code === 'over_budget')).toEqual([
      { code: 'over_budget', userId: BRAM, weekIndex: 0, period: 'weekend', minutes: 170, budget: 120 },
    ]);
    expect(budgetFor({ ...users[0]!, maxDailyMinutes: { weekday: 45, weekend: 75 } }, 6)).toBe(75);
    expect(budgetFor({ ...users[0]!, maxDailyMinutes: { weekday: 45, weekend: 75 } }, 5)).toBe(45);
  });

  it('exactly at budget is fine', () => {
    const result = run([slot('weekly', 0, 1), slot('quarter', 0, 1, ANNA)], {
      tasks: [task('weekly', '1w', 60), QUARTER],
      users: [{ ...users[1]! }, { ...users[0]!, dailyBudgetMinutes: { weekday: 90, weekend: 120 } }],
    });
    expect(result.warnings.filter((w) => w.code === 'over_budget')).toEqual([]);
  });

  it('applies one shared weekday budget across Monday to Friday', () => {
    const result = run([slot('weekly', 0, 1), slot('monthly', 0, 2)], { tasks: [WEEKLY, MONTHLY] });
    expect(result.warnings.filter((w) => w.code === 'over_budget')).toEqual([
      { code: 'over_budget', userId: BRAM, weekIndex: 0, period: 'weekday', minutes: 80, budget: 60 },
    ]);
    expect(
      result.summary.days
        .filter((day) => day.weekIndex === 0 && [1, 2].includes(day.weekday))
        .map((day) => day.users.find((user) => user.userId === BRAM)?.overBudget),
    ).toEqual([false, false]);
  });

  it('summarises minutes per day per user with budgets, Monday first, for active users only', () => {
    const { days } = run([slot('weekly', 0, 1, ANNA), slot('quarter', 0, 1, BRAM)]).summary;
    expect(days).toHaveLength(28);
    expect(days.slice(0, 7).map((d) => d.weekday)).toEqual([1, 2, 3, 4, 5, 6, 0]);
    expect(days[0]).toEqual({
      weekIndex: 0,
      weekday: 1,
      users: [
        { userId: ANNA, minutes: 40, budget: 480, overBudget: false },
        { userId: BRAM, minutes: 90, budget: 480, overBudget: false },
      ],
      unassignedMinutes: 0,
    });
    expect(days[5]!.users[0]).toMatchObject({ budget: 480 });
  });

  it('warns when one day exceeds the configured daily maximum', () => {
    const result = run([slot('weekly', 0, 1), slot('monthly', 0, 1)], {
      tasks: [WEEKLY, MONTHLY],
      users: users.map((user) =>
        user._id === BRAM
          ? { ...user, dailyBudgetMinutes: { weekday: 200, weekend: 200 }, maxDailyMinutes: { weekday: 60, weekend: 90 } }
          : user,
      ),
    });
    expect(result.warnings.filter((warning) => warning.code === 'daily_over_budget')).toEqual([
      { code: 'daily_over_budget', userId: BRAM, weekIndex: 0, weekday: 1, minutes: 80, budget: 60 },
    ]);
  });

  it('totals unassigned slots separately, not against any budget', () => {
    const result = run([slot('quarter', 2, 3, null), slot('weekly', 2, 3, null), slot('twice', 2, 4, ANNA)]);
    expect(result.warnings.filter((w) => w.code === 'over_budget')).toEqual([]);
    const wednesday = result.summary.days.find((d) => d.weekIndex === 2 && d.weekday === 3)!;
    expect(wednesday.unassignedMinutes).toBe(130);
    expect(wednesday.users.every((u) => u.minutes === 0)).toBe(true);
    expect(result.summary.weeks[2]).toEqual({
      weekIndex: 2,
      users: [
        { userId: ANNA, minutes: 10 },
        { userId: BRAM, minutes: 0 },
      ],
      unassignedMinutes: 130,
    });
  });

  it('totals minutes per week per user', () => {
    const { weeks } = run(fullPlan()).summary;
    expect(weeks.map((w) => w.users.find((u) => u.userId === BRAM)!.minutes)).toEqual([60, 60, 60, 60]);
  });
});

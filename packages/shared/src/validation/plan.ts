import type { Interval } from '../schemas/intervals.ts';

/**
 * Shared plan validation: used by the template editor (client), PUT /slots
 * (server) and AI proposal validation. Pure; ids are strings.
 */

export interface PlanSlot {
  taskId: string;
  weekIndex: number;
  /** 0=Sunday..6=Saturday */
  weekday: number;
  assigneeId: string | null;
}

export interface PlanTask {
  _id: string;
  name: string;
  intervalKey: string;
  durationMinutes: number;
  active: boolean;
}

export interface PlanUser {
  _id: string;
  name: string;
  active: boolean;
  unavailableWeekdays: number[];
  dailyBudgetMinutes: { weekday: number; weekend: number };
  maxDailyMinutes: { weekday: number; weekend: number };
}

export interface ValidatePlanInput {
  slots: PlanSlot[];
  tasks: PlanTask[];
  users: PlanUser[];
  intervals: Interval[];
}

export type PlanErrorCode =
  | 'week_index_out_of_range'
  | 'weekday_out_of_range'
  | 'unknown_task'
  | 'inactive_task'
  | 'unknown_user'
  | 'inactive_user'
  | 'assignee_unavailable'
  | 'duplicate_task_day';

export type PlanWarningCode = 'interval_mismatch' | 'over_budget' | 'daily_over_budget';

export interface PlanIssue<C extends string> {
  code: C;
  /** Index into `slots`, for slot-level issues. */
  slotIndex?: number;
  taskId?: string;
  userId?: string;
  weekIndex?: number;
  weekday?: number;
  period?: 'weekday' | 'weekend';
  placed?: number;
  required?: number;
  minutes?: number;
  budget?: number;
}

export type PlanError = PlanIssue<PlanErrorCode>;
export type PlanWarning = PlanIssue<PlanWarningCode>;

export interface TaskSummary {
  taskId: string;
  placed: number;
  /** null when the interval is not planned via the grid (e.g. quarter). */
  required: number | null;
}

export interface UserMinutes {
  userId: string;
  minutes: number;
}

export interface UserDayMinutes extends UserMinutes {
  budget: number;
  overBudget: boolean;
}

export interface DaySummary {
  weekIndex: number;
  weekday: number;
  users: UserDayMinutes[];
  /** Minutes of slots without assignee ("wie dan ook"), not counted against any budget. */
  unassignedMinutes: number;
}

export interface WeekSummary {
  weekIndex: number;
  users: UserMinutes[];
  unassignedMinutes: number;
}

export interface PlanSummary {
  tasks: TaskSummary[];
  days: DaySummary[];
  weeks: WeekSummary[];
}

export interface PlanValidationResult {
  errors: PlanError[];
  warnings: PlanWarning[];
  summary: PlanSummary;
}

export const PLAN_WEEKS = 4;
/** Monday-first display order of 0=Sunday..6=Saturday weekdays. */
export const WEEKDAYS_MONDAY_FIRST = [1, 2, 3, 4, 5, 6, 0] as const;

export function isWeekendDay(weekday: number): boolean {
  return weekday === 0 || weekday === 6;
}

export function budgetFor(user: PlanUser, weekday: number): number {
  return isWeekendDay(weekday) ? user.maxDailyMinutes.weekend : user.maxDailyMinutes.weekday;
}

const inRange = (n: number, min: number, max: number) => Number.isInteger(n) && n >= min && n <= max;

const dayKey = (weekIndex: number, weekday: number) => `${weekIndex}:${weekday}`;

export function validatePlan({ slots, tasks, users, intervals }: ValidatePlanInput): PlanValidationResult {
  const errors: PlanError[] = [];
  const warnings: PlanWarning[] = [];

  const taskById = new Map(tasks.map((t) => [t._id, t]));
  const userById = new Map(users.map((u) => [u._id, u]));
  const intervalByKey = new Map(intervals.map((i) => [i.key, i]));
  const activeUsers = users.filter((u) => u.active);

  const placed = new Map<string, number>();
  const seenTaskDay = new Set<string>();
  // minutes per day per user; key `${weekIndex}:${weekday}` → userId → minutes
  const dayMinutes = new Map<string, Map<string, number>>();
  const dayUnassigned = new Map<string, number>();

  slots.forEach((slot, slotIndex) => {
    const base = { slotIndex, taskId: slot.taskId, weekIndex: slot.weekIndex, weekday: slot.weekday };

    let positionOk = true;
    if (!inRange(slot.weekIndex, 0, PLAN_WEEKS - 1)) {
      errors.push({ code: 'week_index_out_of_range', ...base });
      positionOk = false;
    }
    if (!inRange(slot.weekday, 0, 6)) {
      errors.push({ code: 'weekday_out_of_range', ...base });
      positionOk = false;
    }

    const task = taskById.get(slot.taskId);
    if (!task) errors.push({ code: 'unknown_task', ...base });
    else if (!task.active) errors.push({ code: 'inactive_task', ...base });

    let assigneeOk = true;
    if (slot.assigneeId !== null) {
      const user = userById.get(slot.assigneeId);
      if (!user) {
        errors.push({ code: 'unknown_user', ...base, userId: slot.assigneeId });
        assigneeOk = false;
      } else if (!user.active) {
        errors.push({ code: 'inactive_user', ...base, userId: slot.assigneeId });
        assigneeOk = false;
      } else if (positionOk && user.unavailableWeekdays.includes(slot.weekday)) {
        errors.push({ code: 'assignee_unavailable', ...base, userId: slot.assigneeId });
      }
    }

    if (!positionOk) return;

    const key = dayKey(slot.weekIndex, slot.weekday);
    const taskDay = `${slot.taskId}@${key}`;
    if (seenTaskDay.has(taskDay)) errors.push({ code: 'duplicate_task_day', ...base });
    seenTaskDay.add(taskDay);

    if (!task) return;
    placed.set(task._id, (placed.get(task._id) ?? 0) + 1);

    if (slot.assigneeId === null) {
      dayUnassigned.set(key, (dayUnassigned.get(key) ?? 0) + task.durationMinutes);
    } else if (assigneeOk) {
      const perUser = dayMinutes.get(key) ?? new Map<string, number>();
      perUser.set(slot.assigneeId, (perUser.get(slot.assigneeId) ?? 0) + task.durationMinutes);
      dayMinutes.set(key, perUser);
    }
  });

  // Per-task placed/required: every active task, plus inactive ones that still have slots.
  const summaryTasks: TaskSummary[] = [];
  for (const task of tasks) {
    const count = placed.get(task._id) ?? 0;
    if (!task.active && count === 0) continue;
    const required = intervalByKey.get(task.intervalKey)?.perCycle ?? null;
    summaryTasks.push({ taskId: task._id, placed: count, required });
    if (task.active && required !== null && count !== required) {
      warnings.push({ code: 'interval_mismatch', taskId: task._id, placed: count, required });
    }
  }

  const days: DaySummary[] = [];
  const weeks: WeekSummary[] = [];
  for (let weekIndex = 0; weekIndex < PLAN_WEEKS; weekIndex++) {
    const weekTotals = new Map(activeUsers.map((u) => [u._id, 0]));
    const weekdayTotals = new Map(activeUsers.map((u) => [u._id, 0]));
    const weekendTotals = new Map(activeUsers.map((u) => [u._id, 0]));
    let weekUnassigned = 0;
    for (const weekday of WEEKDAYS_MONDAY_FIRST) {
      const perUser = dayMinutes.get(dayKey(weekIndex, weekday));
      const periodTotals = isWeekendDay(weekday) ? weekendTotals : weekdayTotals;
      for (const user of activeUsers) {
        periodTotals.set(user._id, (periodTotals.get(user._id) ?? 0) + (perUser?.get(user._id) ?? 0));
      }
    }
    for (const user of activeUsers) {
      for (const period of ['weekday', 'weekend'] as const) {
        const minutes = (period === 'weekday' ? weekdayTotals : weekendTotals).get(user._id) ?? 0;
        const budget = user.dailyBudgetMinutes[period];
        if (minutes > budget) {
          warnings.push({ code: 'over_budget', userId: user._id, weekIndex, period, minutes, budget });
        }
      }
    }
    for (const weekday of WEEKDAYS_MONDAY_FIRST) {
      const key = dayKey(weekIndex, weekday);
      const perUser = dayMinutes.get(key);
      const unassignedMinutes = dayUnassigned.get(key) ?? 0;
      weekUnassigned += unassignedMinutes;
      const dayUsers = activeUsers.map((user) => {
        const minutes = perUser?.get(user._id) ?? 0;
        const budget = budgetFor(user, weekday);
        // A day card only flags a day that exceeds the complete period budget by itself.
        // The aggregate Monday-Friday / weekend check is reported once above.
        const overBudget = minutes > budget;
        if (overBudget) {
          warnings.push({ code: 'daily_over_budget', userId: user._id, weekIndex, weekday, minutes, budget });
        }
        weekTotals.set(user._id, (weekTotals.get(user._id) ?? 0) + minutes);
        return { userId: user._id, minutes, budget, overBudget };
      });
      days.push({ weekIndex, weekday, users: dayUsers, unassignedMinutes });
    }
    weeks.push({
      weekIndex,
      users: activeUsers.map((u) => ({ userId: u._id, minutes: weekTotals.get(u._id) ?? 0 })),
      unassignedMinutes: weekUnassigned,
    });
  }

  return { errors, warnings, summary: { tasks: summaryTasks, days, weeks } };
}

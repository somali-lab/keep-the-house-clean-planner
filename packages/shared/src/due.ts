import type { Interval } from './schemas/intervals.ts';
import { APP_TIMEZONE, daysBetween, toDayKey, type DayKey } from './time.ts';

/**
 * The "hybrid" half of scheduling: a task starts on its initial due date;
 * after its first completion, elapsed time is compared with its interval.
 */

export type DueState = 'ok' | 'due' | 'overdue';

export const DUE_RATIO = 1.0;
export const OVERDUE_RATIO = 1.5;

export interface DueTaskInput {
  _id: string;
  active: boolean;
  intervalKey: string;
  lastCompletedAt: Date | string | null;
  initialDueDate: DayKey;
}

export interface DueResult {
  taskId: string;
  /** Effective age in days; starts at one interval on the initial due date. */
  daysSince: number;
  periodDays: number;
  ratio: number;
  state: DueState;
}

export function dueState(ratio: number): DueState {
  if (ratio >= OVERDUE_RATIO) return 'overdue';
  if (ratio >= DUE_RATIO) return 'due';
  return 'ok';
}

/**
 * Ranked due list for active tasks, highest ratio first. Days are counted in
 * local calendar days (DST-safe). Before a never-completed task's initial due
 * date its effective age is zero. Vacation days count too, and a skipped
 * occurrence does not change `lastCompletedAt`, so skipping keeps a task due.
 * Tasks with an unknown interval key are left out.
 */
export function computeDue(
  tasks: DueTaskInput[],
  intervals: Interval[],
  today: DayKey,
  timezone: string = APP_TIMEZONE,
): DueResult[] {
  const periodByKey = new Map(intervals.map((i) => [i.key, i.periodDays]));
  const results: DueResult[] = [];
  for (const task of tasks) {
    if (!task.active) continue;
    const periodDays = periodByKey.get(task.intervalKey);
    if (!periodDays) continue;
    const daysSince = task.lastCompletedAt
      ? Math.max(0, daysBetween(toDayKey(new Date(task.lastCompletedAt), timezone), today))
      : today < task.initialDueDate
        ? 0
        : periodDays + daysBetween(task.initialDueDate, today);
    const ratio = daysSince / periodDays;
    results.push({ taskId: task._id, daysSince, periodDays, ratio, state: dueState(ratio) });
  }
  return results.sort(
    (a, b) => b.ratio - a.ratio || b.daysSince - a.daysSince || a.taskId.localeCompare(b.taskId),
  );
}

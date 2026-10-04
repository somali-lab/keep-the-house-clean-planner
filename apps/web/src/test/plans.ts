import type { User } from '@huishoudplanner/shared';
import type { CyclePlan, PlanIssue, PlanSlot, PlanValidation } from '../features/planner/api.ts';
import type { Task } from '../api/v2/queries.ts';

const STAMP = '2026-09-14T08:00:00.000Z';

/** A plan as `GET /api/v2/cycle-plans` returns it (field `id`, with its version). */
export function makePlanV2(overrides: Partial<CyclePlan> & Pick<CyclePlan, 'id' | 'name'>): CyclePlan {
  return {
    active: false,
    slots: [],
    weekThemes: ['', '', '', ''],
    draft: false,
    source: 'manual',
    proposalId: null,
    rationale: null,
    discarded: false,
    createdAt: STAMP,
    updatedAt: STAMP,
    version: 1,
    ...overrides,
  };
}

export const slotOf = (taskId: string, weekIndex: number, weekday: number, assigneeId: string | null, sortOrder = 0): PlanSlot => ({
  taskId,
  weekIndex,
  weekday,
  assigneeId,
  sortOrder,
});

export const issueOf = (overrides: Partial<PlanIssue> & Pick<PlanIssue, 'code'>): PlanIssue => ({
  slotIndex: null,
  taskId: null,
  userId: null,
  weekIndex: null,
  weekday: null,
  period: null,
  placed: null,
  required: null,
  minutes: null,
  budget: null,
  ...overrides,
});

/** How many slots an interval asks for in the grid of four weeks; the test intervals only. Null = not planned through the grid. */
const REQUIRED: Record<string, number | null> = { '1w': 4, '2w': 2, quarter: null };

/**
 * A stand-in for `POST /api/v2/cycle-plans/validation`: the summary of the slots (placed against required per task, minutes and budget per
 * person per day and week). The rules of the real validation live in the .NET domain; the tests only need a server that answers with a
 * consistent summary, so the web code under test can be shown to render it and not to compute it.
 */
export function standInValidation(slots: readonly PlanSlot[], tasks: readonly Task[], users: readonly User[], issues: PlanIssue[] = []): PlanValidation {
  const minutesOf = (slot: PlanSlot) => tasks.find((task) => task.id === slot.taskId)?.durationMinutes ?? 0;
  const days = [0, 1, 2, 3].flatMap((weekIndex) =>
    [0, 1, 2, 3, 4, 5, 6].map((weekday) => {
      const here = slots.filter((slot) => slot.weekIndex === weekIndex && slot.weekday === weekday);
      return {
        weekIndex,
        weekday,
        users: users.map((user) => {
          const minutes = here.filter((slot) => slot.assigneeId === user._id).reduce((sum, slot) => sum + minutesOf(slot), 0);
          const budget = weekday === 0 || weekday === 6 ? user.dailyBudgetMinutes.weekend : user.dailyBudgetMinutes.weekday;
          return { userId: user._id, minutes, budget, overBudget: minutes > budget };
        }),
        unassignedMinutes: here.filter((slot) => slot.assigneeId === null).reduce((sum, slot) => sum + minutesOf(slot), 0),
      };
    }),
  );
  return {
    valid: issues.length === 0,
    errors: {},
    issues,
    warnings: [],
    summary: {
      tasks: tasks.map((task) => ({
        taskId: task.id,
        placed: slots.filter((slot) => slot.taskId === task.id).length,
        required: REQUIRED[task.intervalKey] ?? null,
      })),
      days,
      weeks: [0, 1, 2, 3].map((weekIndex) => ({
        weekIndex,
        users: users.map((user) => ({
          userId: user._id,
          minutes: days.filter((day) => day.weekIndex === weekIndex).reduce((sum, day) => sum + (day.users.find((u) => u.userId === user._id)?.minutes ?? 0), 0),
        })),
        unassignedMinutes: days.filter((day) => day.weekIndex === weekIndex).reduce((sum, day) => sum + day.unassignedMinutes, 0),
      })),
    },
  };
}

/** A `412 precondition_failed` answer carrying the current ETag. */
export function staleAnswer(version: number) {
  return () =>
    new Response(
      JSON.stringify({ type: 'urn:huishoudplanner:problem:precondition_failed', title: 'Precondition Failed', status: 412, detail: 'stale', traceId: 't' }),
      { status: 412, headers: { 'Content-Type': 'application/problem+json', ETag: `"${version}"` } },
    );
}

export const STALE_MESSAGE = 'Deze gegevens zijn intussen door iemand anders gewijzigd. Controleer je wijziging en sla opnieuw op.';

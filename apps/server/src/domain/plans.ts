import { validatePlan, type PlanValidationResult, type Slot } from '@huishoudplanner/shared';
import { ObjectId, type Db } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { countPlans, createPlan, EMPTY_WEEK_THEMES, type SlotDoc } from '../data/cyclePlans.ts';
import { getSettings } from '../data/settings.ts';
import { listTasks } from '../data/tasks.ts';
import { listUsers } from '../data/users.ts';

export const DEFAULT_PLAN_NAME = 'Standaard';

/** First start without plans: an empty, active plan. Idempotent. */
export async function ensureDefaultPlan(ctx: AuditContext): Promise<boolean> {
  if ((await countPlans(ctx.db)) > 0) return false;
  await createPlan(ctx, {
    name: DEFAULT_PLAN_NAME,
    active: true,
    slots: [],
    weekThemes: EMPTY_WEEK_THEMES,
    draft: false,
    source: 'manual',
    proposalId: null,
    rationale: null,
    discarded: false,
  });
  return true;
}

export function slotsToDocs(slots: Slot[]): SlotDoc[] {
  return slots.map((s) => ({
    taskId: new ObjectId(s.taskId),
    weekIndex: s.weekIndex,
    weekday: s.weekday,
    assigneeId: s.assigneeId === null ? null : new ObjectId(s.assigneeId),
    sortOrder: s.sortOrder,
  }));
}

/** Runs the shared plan validation against the current tasks, users and intervals. */
export async function validateSlotsAgainstDb(db: Db, slots: SlotDoc[]): Promise<PlanValidationResult> {
  const [tasks, users, settings] = await Promise.all([listTasks(db), listUsers(db), getSettings(db)]);
  return validatePlan({
    slots: slots.map((s) => ({
      taskId: s.taskId.toHexString(),
      weekIndex: s.weekIndex,
      weekday: s.weekday,
      assigneeId: s.assigneeId?.toHexString() ?? null,
    })),
    tasks: tasks.map((t) => ({
      _id: t._id.toHexString(),
      name: t.name,
      intervalKey: t.intervalKey,
      durationMinutes: t.durationMinutes,
      active: t.active,
    })),
    users: users.map((u) => ({
      _id: u._id.toHexString(),
      name: u.name,
      active: u.active,
      unavailableWeekdays: u.unavailableWeekdays,
      dailyBudgetMinutes: u.dailyBudgetMinutes,
      maxDailyMinutes: u.maxDailyMinutes,
    })),
    intervals: settings?.intervals ?? [],
  });
}

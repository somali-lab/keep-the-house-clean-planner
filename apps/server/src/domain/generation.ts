import {
  cycleEnd,
  cycleIndexFor,
  cycleStart,
  fromDayKey,
  slotDate,
  today,
  type VacationRange,
} from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { findActivePlan, type CyclePlanDoc } from '../data/cyclePlans.ts';
import { ensureCycle, setCyclePlan, type CycleDoc } from '../data/cycles.ts';
import {
  deleteOccurrences,
  findOccurrences,
  insertOccurrencesIdempotent,
  type OccurrenceDoc,
} from '../data/occurrences.ts';
import { getSettings, type SettingsDoc } from '../data/settings.ts';
import { listTasks } from '../data/tasks.ts';
import { listRooms } from '../data/rooms.ts';
import { HttpError } from '../http/errors.ts';

export function isInVacation(dayKey: string, ranges: VacationRange[]): boolean {
  return ranges.some((r) => r.from <= dayKey && dayKey <= r.to);
}

async function requireSettings(ctx: AuditContext): Promise<SettingsDoc> {
  const settings = await getSettings(ctx.db);
  if (!settings) throw new HttpError(500, 'settings_missing');
  return settings;
}

export interface GenerationResult {
  cycleIndex: number;
  cycleId: ObjectId;
  planId: ObjectId | null;
  inserted: number;
  skipped: number;
}

/** Cycle index of today in the app timezone. */
export async function currentCycleIndex(ctx: AuditContext): Promise<number> {
  const settings = await requireSettings(ctx);
  return cycleIndexFor(today(settings.timezone, ctx.clock.now()), settings.cycleAnchorDate);
}

/**
 * Generates occurrences for one cycle from the active plan. Idempotent: the
 * unique index (cycleId, taskId, plannedDate) drops duplicates, and only real
 * inserts are audited. Skipped: vacation days, inactive tasks, and days before
 * today (so a plan activated mid-cycle does not create instant overdue items).
 */
export async function generateCycle(
  ctx: AuditContext,
  cycleIndex: number,
  options: { runId: string; plan?: CyclePlanDoc | null },
): Promise<GenerationResult> {
  const settings = await requireSettings(ctx);
  const plan = options.plan === undefined ? await findActivePlan(ctx.db) : options.plan;
  const anchor = settings.cycleAnchorDate;
  const cycle = await ensureCycle(ctx, {
    index: cycleIndex,
    startDate: cycleStart(cycleIndex, anchor),
    endDate: cycleEnd(cycleIndex, anchor),
    planId: plan?._id ?? null,
    runId: options.runId,
  });
  if (!plan) {
    return { cycleIndex, cycleId: cycle._id, planId: null, inserted: 0, skipped: 0 };
  }

  const now = ctx.clock.now();
  const todayKey = today(settings.timezone, now);
  const [taskDocs, rooms] = await Promise.all([listTasks(ctx.db), listRooms(ctx.db)]);
  const tasks = new Map(taskDocs.map((task) => [task._id.toHexString(), task]));
  const roomNames = new Map(rooms.map((room) => [room._id.toHexString(), room.name]));
  const docs: OccurrenceDoc[] = [];
  for (const slot of plan.slots) {
    const task = tasks.get(slot.taskId.toHexString());
    if (!task?.active) continue;
    const dayKey = slotDate(cycle.startDate, slot.weekIndex, slot.weekday);
    if (dayKey < todayKey || isInVacation(dayKey, settings.vacationRanges)) continue;
    const date = fromDayKey(dayKey, settings.timezone);
    docs.push({
      _id: new ObjectId(),
      taskId: task._id,
      cycleId: cycle._id,
      planId: plan._id,
      date,
      plannedDate: date,
      assigneeId: slot.assigneeId,
      status: 'open',
      statusBeforeCompletion: null,
      completedAt: null,
      completedBy: null,
      skipReason: null,
      durationMinutesSnapshot: task.durationMinutes,
      taskNameSnapshot: task.name,
      roomIdSnapshot: task.roomId,
      roomNameSnapshot: roomNames.get(task.roomId.toHexString()) ?? null,
      origin: 'generated',
      createdAt: now,
      updatedAt: now,
    });
  }

  const inserted = await insertOccurrencesIdempotent(ctx, docs, {
    runId: options.runId,
    cycleIndex,
  });
  return {
    cycleIndex,
    cycleId: cycle._id,
    planId: plan._id,
    inserted: inserted.length,
    skipped: docs.length - inserted.length,
  };
}

/** Generates the current and the next cycle (nightly job and on-demand). */
export async function generateUpcoming(
  ctx: AuditContext,
  runId: string,
): Promise<GenerationResult[]> {
  const current = await currentCycleIndex(ctx);
  const plan = await findActivePlan(ctx.db);
  if (plan && (await upcomingOccurrencesNeedReplacement(ctx, plan, current, runId))) {
    return (await replaceUpcomingOccurrences(ctx, plan, runId, 'nightly_reconciliation')).generated;
  }
  return [
    await generateCycle(ctx, current, { runId, plan }),
    await generateCycle(ctx, current + 1, { runId, plan }),
  ];
}

function occurrenceKey(cycleId: ObjectId, taskId: ObjectId, plannedDate: Date): string {
  return `${cycleId.toHexString()}:${taskId.toHexString()}:${plannedDate.getTime()}`;
}

async function upcomingOccurrencesNeedReplacement(
  ctx: AuditContext,
  plan: CyclePlanDoc,
  currentCycle: number,
  runId: string,
): Promise<boolean> {
  const settings = await requireSettings(ctx);
  const todayKey = today(settings.timezone, ctx.clock.now());
  const cycles: CycleDoc[] = [];
  for (const index of [currentCycle, currentCycle + 1]) {
    cycles.push(
      await ensureCycle(ctx, {
        index,
        startDate: cycleStart(index, settings.cycleAnchorDate),
        endDate: cycleEnd(index, settings.cycleAnchorDate),
        planId: plan._id,
        runId,
      }),
    );
  }

  const tasks = new Map(
    (await listTasks(ctx.db)).filter((task) => task.active).map((task) => [task._id.toHexString(), task]),
  );
  const expected = new Map<string, ObjectId | null>();
  for (const cycle of cycles) {
    for (const slot of plan.slots) {
      if (!tasks.has(slot.taskId.toHexString())) continue;
      const dayKey = slotDate(cycle.startDate, slot.weekIndex, slot.weekday);
      if (dayKey < todayKey || isInVacation(dayKey, settings.vacationRanges)) continue;
      const plannedDate = fromDayKey(dayKey, settings.timezone);
      expected.set(occurrenceKey(cycle._id, slot.taskId, plannedDate), slot.assigneeId);
    }
  }

  const occurrences = await findOccurrences(ctx.db, {
    cycleId: { $in: cycles.map((cycle) => cycle._id) },
  });
  const existingKeys = new Set(occurrences.map((occurrence) => occurrenceKey(
    occurrence.cycleId,
    occurrence.taskId,
    occurrence.plannedDate,
  )));
  if ([...expected.keys()].some((key) => !existingKeys.has(key))) return true;

  return occurrences.some((occurrence) => {
    if (occurrence.origin !== 'generated') return false;
    const replaceable =
      occurrence.status === 'open' &&
      occurrence.date >= fromDayKey(todayKey, settings.timezone) &&
      occurrence.date.getTime() === occurrence.plannedDate.getTime();
    if (!replaceable) return false;
    const expectedAssignee = expected.get(
      occurrenceKey(occurrence.cycleId, occurrence.taskId, occurrence.plannedDate),
    );
    return (
      expectedAssignee === undefined ||
      !occurrence.planId?.equals(plan._id) ||
      (expectedAssignee === null
        ? occurrence.assigneeId !== null
        : !occurrence.assigneeId?.equals(expectedAssignee))
    );
  });
}

export interface ReplacementResult {
  removed: number;
  generated: GenerationResult[];
}

/**
 * Replacement rule (plan §1.4) for the current and next cycle: open,
 * generated, not-dragged occurrences from today on are removed (audited) and
 * regenerated from the newly active plan. Done, skipped, dragged and ad-hoc
 * occurrences stay.
 */
export async function replaceUpcomingOccurrences(
  ctx: AuditContext,
  plan: CyclePlanDoc,
  runId: string,
  reason = 'plan_activation',
): Promise<ReplacementResult> {
  const settings = await requireSettings(ctx);
  const todayKey = today(settings.timezone, ctx.clock.now());
  const current = cycleIndexFor(todayKey, settings.cycleAnchorDate);

  const cycles: CycleDoc[] = [];
  for (const index of [current, current + 1]) {
    const cycle = await ensureCycle(ctx, {
      index,
      startDate: cycleStart(index, settings.cycleAnchorDate),
      endDate: cycleEnd(index, settings.cycleAnchorDate),
      planId: plan._id,
      runId,
    });
    cycles.push(await setCyclePlan(ctx, cycle, plan._id, runId));
  }

  const replaceable = await findOccurrences(ctx.db, {
    cycleId: { $in: cycles.map((c) => c._id) },
    status: 'open',
    origin: 'generated',
    date: { $gte: fromDayKey(todayKey, settings.timezone) },
    $expr: { $eq: ['$date', '$plannedDate'] },
  });
  const removed = await deleteOccurrences(ctx, replaceable, { runId, planId: plan._id, reason });

  const generated: GenerationResult[] = [];
  for (const cycle of cycles) {
    generated.push(await generateCycle(ctx, cycle.index, { runId, plan }));
  }
  return { removed, generated };
}

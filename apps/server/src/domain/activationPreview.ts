import { createHash } from 'node:crypto';
import { cycleIndexFor, cycleStart, fromDayKey, toDayKey, today, type ActivationPreview, type ActivationPreviewItem } from '@huishoudplanner/shared';
import type { ObjectId } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { findActivePlan, findPlanById } from '../data/cyclePlans.ts';
import { findCycleByIndex } from '../data/cycles.ts';
import { findOccurrences, type OccurrenceDoc } from '../data/occurrences.ts';
import { getSettings } from '../data/settings.ts';
import { listTasks } from '../data/tasks.ts';
import { listRooms } from '../data/rooms.ts';
import { HttpError } from '../http/errors.ts';
import { plannedOccurrences } from './generation.ts';

const byDateAndId = (a: ActivationPreviewItem, b: ActivationPreviewItem) =>
  a.date.localeCompare(b.date) || a.taskName.localeCompare(b.taskName) ||
  a.taskId.localeCompare(b.taskId) || (a.occurrenceId ?? '').localeCompare(b.occurrenceId ?? '');

/** Read-only simulation of activation. No cycle documents are created here. */
export async function activationPreview(ctx: AuditContext, planId: ObjectId): Promise<ActivationPreview> {
  const [plan, activePlan, settings, tasks, rooms] = await Promise.all([
    findPlanById(ctx.db, planId), findActivePlan(ctx.db), getSettings(ctx.db), listTasks(ctx.db), listRooms(ctx.db),
  ]);
  if (!plan) throw new HttpError(404, 'not_found', 'cycle plan not found');
  if (!settings) throw new HttpError(500, 'settings_missing');
  const asOfDate = today(settings.timezone, ctx.clock.now());
  const current = cycleIndexFor(asOfDate, settings.cycleAnchorDate);
  const endExclusive = cycleStart(current + 2, settings.cycleAnchorDate);
  const cycles = await Promise.all([current, current + 1].map((index) => findCycleByIndex(ctx.db, index)));
  const cycleIds = cycles.filter((cycle) => cycle !== null).map((cycle) => cycle._id);
  const cycleIndexById = new Map(cycles.filter((cycle) => cycle !== null).map((cycle) => [cycle._id.toHexString(), cycle.index]));
  const from = fromDayKey(asOfDate, settings.timezone);
  const to = fromDayKey(endExclusive, settings.timezone);
  const existing = await findOccurrences(ctx.db, {
    $or: [
      { date: { $gte: from, $lt: to } },
      { plannedDate: { $gte: from, $lt: to } },
      ...(cycleIds.length ? [{ cycleId: { $in: cycleIds } }] : []),
    ],
  });
  const inWindow = (date: Date) => date >= from && date < to;
  const replaceable = (occurrence: OccurrenceDoc) =>
    occurrence.status === 'open' && occurrence.origin === 'generated' &&
    inWindow(occurrence.date) &&
    occurrence.date.getTime() === occurrence.plannedDate.getTime();
  const item = (occurrence: OccurrenceDoc): ActivationPreviewItem => ({
    occurrenceId: occurrence._id.toHexString(),
    cycleIndex: cycleIndexById.get(occurrence.cycleId.toHexString()) ??
      cycleIndexFor(toDayKey(occurrence.plannedDate, settings.timezone), settings.cycleAnchorDate),
    taskId: occurrence.taskId.toHexString(),
    taskName: occurrence.taskNameSnapshot,
    date: toDayKey(occurrence.date, settings.timezone),
    assigneeId: occurrence.assigneeId?.toHexString() ?? null,
  });
  const removedDocs = existing.filter(replaceable);
  const removedIds = new Set(removedDocs.map((occurrence) => occurrence._id.toHexString()));
  const removed = removedDocs.map(item).sort(byDateAndId);
  const preserved: ActivationPreview['preserved'] = { done: [], skipped: [], moved: [], adhoc: [] };
  for (const occurrence of existing) {
    if (removedIds.has(occurrence._id.toHexString())) continue;
    if (occurrence.origin === 'adhoc') preserved.adhoc.push(item(occurrence));
    else if (occurrence.status === 'done') preserved.done.push(item(occurrence));
    else if (occurrence.status === 'skipped') preserved.skipped.push(item(occurrence));
    else if (occurrence.date.getTime() !== occurrence.plannedDate.getTime()) preserved.moved.push(item(occurrence));
  }
  for (const group of Object.values(preserved)) group.sort(byDateAndId);

  // The unique index is (cycleId, taskId, plannedDate). Surviving records with
  // that key suppress an insert, including skipped and moved occurrences.
  const occupied = new Set(existing.filter((occurrence) => !removedIds.has(occurrence._id.toHexString()))
    .map((occurrence) => `${occurrence.cycleId.toHexString()}:${occurrence.taskId.toHexString()}:${occurrence.plannedDate.getTime()}`));
  const added: ActivationPreviewItem[] = [];
  for (const cycleIndex of [current, current + 1]) {
    const cycleId = cycles[cycleIndex - current]?._id.toHexString() ?? `new:${cycleIndex}`;
    for (const planned of plannedOccurrences(plan, cycleIndex, settings, tasks, asOfDate)) {
      const instant = fromDayKey(planned.dayKey, settings.timezone).getTime();
      const key = `${cycleId}:${planned.task._id.toHexString()}:${instant}`;
      if (occupied.has(key)) continue;
      occupied.add(key);
      added.push({
        occurrenceId: null,
        cycleIndex,
        taskId: planned.task._id.toHexString(),
        taskName: planned.task.name,
        date: planned.dayKey,
        assigneeId: planned.assigneeId?.toHexString() ?? null,
      });
    }
  }
  added.sort(byDateAndId);
  const visible = { planId: planId.toHexString(), asOfDate, removed, added, preserved };
  const previewToken = createHash('sha256').update(JSON.stringify({
    ...visible,
    plan: {
      name: plan.name,
      slots: plan.slots.map((slot) => ({
        taskId: slot.taskId.toHexString(), weekIndex: slot.weekIndex, weekday: slot.weekday,
        assigneeId: slot.assigneeId?.toHexString() ?? null, sortOrder: slot.sortOrder,
      })),
      updatedAt: plan.updatedAt.toISOString(),
    },
    generationInputs: tasks.filter((task) => plan.slots.some((slot) => slot.taskId.equals(task._id)))
      .map((task) => ({
        id: task._id.toHexString(), active: task.active, name: task.name,
        durationMinutes: task.durationMinutes, roomId: task.roomId.toHexString(),
        roomName: rooms.find((room) => room._id.equals(task.roomId))?.name ?? null,
      })).sort((a, b) => a.id.localeCompare(b.id)),
    occurrenceState: existing.map((occurrence) => ({
      id: occurrence._id.toHexString(),
      cycleId: occurrence.cycleId.toHexString(),
      planId: occurrence.planId?.toHexString() ?? null,
      taskId: occurrence.taskId.toHexString(),
      date: occurrence.date.toISOString(),
      plannedDate: occurrence.plannedDate.toISOString(),
      assigneeId: occurrence.assigneeId?.toHexString() ?? null,
      status: occurrence.status,
      origin: occurrence.origin,
      updatedAt: occurrence.updatedAt.toISOString(),
    })).sort((a, b) => a.id.localeCompare(b.id)),
    activePlanId: activePlan?._id.toHexString() ?? null,
    settings: {
      timezone: settings.timezone,
      cycleAnchorDate: settings.cycleAnchorDate,
      vacationRanges: settings.vacationRanges,
    },
    cycles: cycles.map((cycle) => cycle?._id.toHexString() ?? null),
  })).digest('hex');
  return { ...visible, previewToken };
}

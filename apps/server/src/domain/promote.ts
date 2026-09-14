import {
  cycleIndexFor,
  slotDate,
  today,
  toDayKey,
  weekdaySun0,
  weekIndexFor,
  type ApplyPromotionInput,
  type PlanValidationResult,
  type PromoteSuggestion,
} from '@huishoudplanner/shared';
import { ObjectId, type Db } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { findActivePlan, replaceSlots, type CyclePlanDoc } from '../data/cyclePlans.ts';
import { listCycles, type CycleDoc } from '../data/cycles.ts';
import { findOccurrences, type OccurrenceDoc } from '../data/occurrences.ts';
import { getSettings } from '../data/settings.ts';
import { listTasks } from '../data/tasks.ts';
import { HttpError } from '../http/errors.ts';
import { validateSlotsAgainstDb } from './plans.ts';

interface Move {
  id: string;
  weekday: number;
  assigneeId: string | null;
}

/**
 * Plan §1.4: suggest changing a template slot when, in the last
 * `promoteThreshold` consecutive cycles, its occurrence was dragged to the
 * same weekday within the same week (and, optionally, always to the same
 * other person). Cycles that have not been decided yet (planned in the future
 * and not moved) are ignored. Dismissed suggestions return only with newer evidence.
 */
export async function computePromoteSuggestions(db: Db, now: Date): Promise<PromoteSuggestion[]> {
  const settings = await getSettings(db);
  const plan = await findActivePlan(db);
  if (!settings || !plan) return [];

  const tz = settings.timezone;
  const anchor = settings.cycleAnchorDate;
  const todayKey = today(tz, now);
  const threshold = settings.promoteThreshold;

  const [cycles, occurrences, tasks] = await Promise.all([
    listCycles(db),
    findOccurrences(db, { planId: plan._id, origin: 'generated' }),
    listTasks(db),
  ]);
  const newestFirst = [...cycles].sort((a, b) => b.index - a.index);
  const taskName = new Map(tasks.map((t) => [t._id.toHexString(), t.name]));
  const byPlannedDay = new Map<string, OccurrenceDoc>();
  for (const occ of occurrences) {
    byPlannedDay.set(`${occ.taskId.toHexString()}|${toDayKey(occ.plannedDate, tz)}`, occ);
  }

  const suggestions: PromoteSuggestion[] = [];
  for (const slot of plan.slots) {
    const taskId = slot.taskId.toHexString();
    const history: { cycle: CycleDoc; occ: OccurrenceDoc; plannedKey: string; dateKey: string }[] = [];
    for (const cycle of newestFirst) {
      const plannedKey = slotDate(cycle.startDate, slot.weekIndex, slot.weekday);
      const occ = byPlannedDay.get(`${taskId}|${plannedKey}`);
      if (occ) history.push({ cycle, occ, plannedKey, dateKey: toDayKey(occ.date, tz) });
    }

    // Skip cycles still to come that nobody has touched yet.
    let start = 0;
    while (start < history.length && history[start]!.dateKey === history[start]!.plannedKey && history[start]!.plannedKey > todayKey) {
      start++;
    }
    const recent = history.slice(start, start + threshold);
    if (recent.length < threshold) continue;
    if (!recent.every((entry, i) => entry.cycle.index === recent[0]!.cycle.index - i)) continue;

    const moves: (Move | null)[] = recent.map(({ occ, cycle, plannedKey, dateKey }) => {
      if (dateKey === plannedKey) return null;
      if (cycleIndexFor(dateKey, anchor) !== cycle.index || weekIndexFor(dateKey, anchor) !== slot.weekIndex) return null;
      return { id: occ._id.toHexString(), weekday: weekdaySun0(dateKey), assigneeId: occ.assigneeId?.toHexString() ?? null };
    });
    if (moves.some((m) => m === null)) continue;
    const valid = moves as Move[];

    const toWeekday = valid[0]!.weekday;
    if (!valid.every((m) => m.weekday === toWeekday)) continue;

    const slotAssignee = slot.assigneeId?.toHexString() ?? null;
    const firstAssignee = valid[0]!.assigneeId;
    const toAssigneeId =
      firstAssignee !== null && firstAssignee !== slotAssignee && valid.every((m) => m.assigneeId === firstAssignee)
        ? firstAssignee
        : undefined;

    const evidence = valid.map((m) => m.id);
    const dismissed = settings.dismissedPromotions.some(
      (d) =>
        d.planId === plan._id.toHexString() &&
        d.taskId === taskId &&
        d.weekIndex === slot.weekIndex &&
        d.weekday === slot.weekday &&
        d.toWeekday === toWeekday &&
        d.toAssigneeId === (toAssigneeId ?? null) &&
        d.lastEvidenceId === evidence[0],
    );
    if (dismissed) continue;

    suggestions.push({
      planId: plan._id.toHexString(),
      taskId,
      taskName: taskName.get(taskId) ?? '',
      fromSlot: { weekIndex: slot.weekIndex, weekday: slot.weekday, assigneeId: slotAssignee },
      toWeekday,
      ...(toAssigneeId ? { toAssigneeId } : {}),
      evidence,
    });
  }
  return suggestions;
}

/** Moves the slot in the active plan, validated with the same rules as the editor (errors → 422). */
export async function applyPromotion(
  ctx: AuditContext,
  input: ApplyPromotionInput,
): Promise<{ plan: CyclePlanDoc; validation: PlanValidationResult }> {
  const plan = await findActivePlan(ctx.db);
  if (!plan || plan._id.toHexString() !== input.planId) {
    throw new HttpError(409, 'plan_not_active', 'Suggestions can only change the active plan');
  }
  const index = plan.slots.findIndex(
    (s) => s.taskId.toHexString() === input.taskId && s.weekIndex === input.weekIndex && s.weekday === input.weekday,
  );
  const original = plan.slots[index];
  if (!original) throw new HttpError(404, 'slot_not_found', 'The slot no longer exists in the plan');

  const moved = {
    ...original,
    weekday: input.toWeekday,
    ...(input.toAssigneeId ? { assigneeId: new ObjectId(input.toAssigneeId) } : {}),
  };
  const slots = plan.slots.map((s, i) => (i === index ? moved : s));
  const validation = await validateSlotsAgainstDb(ctx.db, slots);
  if (validation.errors.length > 0) {
    throw new HttpError(422, 'invalid_plan', 'The changed plan violates hard rules', validation);
  }

  const updated = await replaceSlots(ctx, plan._id, slots, {
    promotedFrom: { weekIndex: original.weekIndex, weekday: original.weekday, assigneeId: original.assigneeId },
    toWeekday: input.toWeekday,
    ...(input.toAssigneeId ? { toAssigneeId: input.toAssigneeId } : {}),
  });
  if (!updated) throw new HttpError(404, 'not_found', 'cycle plan not found');
  return { plan: updated, validation };
}

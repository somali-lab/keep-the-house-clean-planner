import { randomUUID } from 'node:crypto';
import { DEFAULT_AI_PROMPTS, aiPlanOutputSchema, type AiPlanOutput, type PlanError, type PlanWarning } from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import type { AuditContext } from '../../audit/context.ts';
import { createPlan, EMPTY_WEEK_THEMES, type CyclePlanDoc, type SlotDoc, type WeekThemes } from '../../data/cyclePlans.ts';
import { listRooms } from '../../data/rooms.ts';
import { getSettings } from '../../data/settings.ts';
import { listTasks } from '../../data/tasks.ts';
import { listUsers } from '../../data/users.ts';
import { HttpError } from '../../http/errors.ts';
import { validateSlotsAgainstDb } from '../plans.ts';
import type { AiProvider } from './provider.ts';
import { buildPlanJsonSchema, buildPlanPrompt, PLAN_PROPOSAL_REQUEST, type PlanPromptPayload } from './prompt.ts';

export interface ProposalOptions {
  mode: 'propose' | 'rebalance';
  /** Limit to these tasks (propose). Omitted = all active tasks. */
  taskIds?: ObjectId[];
  /** The plan to rebalance. */
  basePlan?: CyclePlanDoc;
  constraints?: string;
}

export interface ProposalResult {
  plan: CyclePlanDoc;
  proposalId: string;
  warnings: PlanWarning[];
  rationale: WeekThemes;
}

type Attempt =
  | { ok: true; slots: SlotDoc[]; rationale: WeekThemes; warnings: PlanWarning[] }
  | { ok: false; errors: string[] };

const WEEKDAYS_MONDAY_FIRST = [1, 2, 3, 4, 5, 6, 0] as const;

/**
 * Small local models regularly return a valid but incomplete slot array. Keep
 * their valid choices and fill every missing recurring occurrence evenly over
 * the 28-day cycle. Tasks without a per-cycle count do not belong in the grid.
 */
function completeRequiredOccurrences(payload: PlanPromptPayload, proposed: AiPlanOutput['slots']): AiPlanOutput['slots'] {
  const completed: AiPlanOutput['slots'] = [];
  const userMinutes = new Map(payload.users.map((user) => [user.id, 0]));
  const userDayMinutes = new Map<string, number>();

  const addMinutes = (assigneeId: string, weekIndex: number, weekday: number, minutes: number) => {
    userMinutes.set(assigneeId, (userMinutes.get(assigneeId) ?? 0) + minutes);
    const key = `${assigneeId}:${weekIndex}:${weekday}`;
    userDayMinutes.set(key, (userDayMinutes.get(key) ?? 0) + minutes);
  };

  const chooseAssignee = (weekIndex: number, weekday: number, durationMinutes: number): string | null => {
    const present = payload.users.filter((user) => !user.unavailableWeekdays.includes(weekday));
    const withinDailyLimit = present.filter((user) => {
      const limit = weekday === 0 || weekday === 6 ? user.maxDailyMinutes.weekend : user.maxDailyMinutes.weekday;
      return (userDayMinutes.get(`${user.id}:${weekIndex}:${weekday}`) ?? 0) + durationMinutes <= limit;
    });
    // Availability is a hard constraint. The daily maximum remains a warning,
    // so an available person is still preferable to an unassigned/shared slot.
    const candidates = withinDailyLimit.length > 0 ? withinDailyLimit : present;
    if (candidates.length === 0) return null;
    candidates.sort((a, b) => (userMinutes.get(a.id) ?? 0) - (userMinutes.get(b.id) ?? 0));
    return candidates[0]!.id;
  };

  const assignConcreteUser = (slot: AiPlanOutput['slots'][number], durationMinutes: number) => {
    const assigneeId = slot.assigneeId ?? chooseAssignee(slot.weekIndex, slot.weekday, durationMinutes);
    return { ...slot, assigneeId };
  };

  payload.tasks.forEach((task, taskIndex) => {
    const occupied = new Set<string>();
    if (task.perCycle === null) {
      for (const slot of proposed) {
        if (slot.taskId !== task.id) continue;
        const key = `${slot.weekIndex}:${slot.weekday}`;
        if (occupied.has(key)) continue;
        occupied.add(key);
        const assigned = assignConcreteUser(slot, task.durationMinutes);
        completed.push(assigned);
        if (assigned.assigneeId !== null) addMinutes(assigned.assigneeId, assigned.weekIndex, assigned.weekday, task.durationMinutes);
      }
      return;
    }

    const required = Math.min(task.perCycle, 28);
    if (required === 0) return;

    for (const slot of proposed) {
      if (slot.taskId !== task.id || completed.filter((item) => item.taskId === task.id).length >= required) continue;
      const key = `${slot.weekIndex}:${slot.weekday}`;
      if (occupied.has(key)) continue;
      occupied.add(key);
      const assigned = assignConcreteUser(slot, task.durationMinutes);
      completed.push(assigned);
      if (assigned.assigneeId !== null) addMinutes(assigned.assigneeId, assigned.weekIndex, assigned.weekday, task.durationMinutes);
    }

    const offset = (taskIndex * 3) % 28;
    const idealPositions = Array.from({ length: required }, (_, index) => (Math.floor((index * 28) / required) + offset) % 28);
    const allPositions = Array.from({ length: 28 }, (_, index) => (offset + index) % 28);
    for (const position of [...new Set([...idealPositions, ...allPositions])]) {
      if (occupied.size >= required) break;
      const weekIndex = Math.floor(position / 7);
      const weekday = WEEKDAYS_MONDAY_FIRST[position % 7]!;
      const key = `${weekIndex}:${weekday}`;
      if (occupied.has(key)) continue;
      const assigneeId = chooseAssignee(weekIndex, weekday, task.durationMinutes);
      occupied.add(key);
      completed.push({ taskId: task.id, weekIndex, weekday, assigneeId });
      if (assigneeId !== null) addMinutes(assigneeId, weekIndex, weekday, task.durationMinutes);
    }
  });

  return completed;
}

function describePlanError(error: PlanError): string {
  const where = [
    error.slotIndex === undefined ? null : `slot ${error.slotIndex}`,
    error.taskId ? `task ${error.taskId}` : null,
    error.userId ? `user ${error.userId}` : null,
    error.weekIndex === undefined ? null : `weekIndex ${error.weekIndex}`,
    error.weekday === undefined ? null : `weekday ${error.weekday}`,
  ].filter(Boolean);
  return `${error.code}${where.length ? ` (${where.join(', ')})` : ''}`;
}

/** Tolerates a Markdown code fence around the JSON; nothing else. */
function stripFence(raw: string): string {
  const fenced = /^\s*```(?:json)?\s*([\s\S]*?)\s*```\s*$/i.exec(raw);
  return fenced ? fenced[1]! : raw;
}

/**
 * Plan §T3.2: ask the provider, parse, check the output contract, run the same
 * validatePlan as the editor. One re-prompt with the error list; if that also
 * fails → 422 `ai_invalid_plan` and nothing is stored. Success is always a
 * draft, never activated.
 */
export async function generatePlanProposal(
  ctx: AuditContext,
  provider: AiProvider,
  options: ProposalOptions,
): Promise<ProposalResult> {
  const settings = await getSettings(ctx.db);
  if (!settings) throw new HttpError(500, 'settings_missing');

  const [allTasks, users, rooms] = await Promise.all([listTasks(ctx.db, { active: true }), listUsers(ctx.db, { active: true }), listRooms(ctx.db)]);
  let tasks = allTasks;
  if (options.taskIds) {
    const wanted = new Set(options.taskIds.map((id) => id.toHexString()));
    tasks = allTasks.filter((t) => wanted.has(t._id.toHexString()));
    if (tasks.length !== wanted.size) {
      throw new HttpError(400, 'validation_error', 'Unknown or inactive tasks', [{ field: 'taskIds', message: 'unknown_task' }]);
    }
  }
  if (tasks.length === 0) {
    throw new HttpError(400, 'validation_error', 'There are no active tasks to plan', [{ field: 'taskIds', message: 'no_tasks' }]);
  }

  const roomName = new Map(rooms.map((r) => [r._id.toHexString(), r.name]));
  const interval = new Map(settings.intervals.map((i) => [i.key, i]));
  const allowedTaskIds = new Set(tasks.map((t) => t._id.toHexString()));

  const basePayload: PlanPromptPayload = {
    mode: options.mode,
    tasks: tasks.map((t) => ({
      id: t._id.toHexString(),
      name: t.name,
      room: roomName.get(t.roomId.toHexString()) ?? null,
      intervalKey: t.intervalKey,
      intervalLabel: interval.get(t.intervalKey)?.label ?? t.intervalKey,
      perCycle: interval.get(t.intervalKey)?.perCycle ?? null,
      periodDays: interval.get(t.intervalKey)?.periodDays ?? 0,
      durationMinutes: t.durationMinutes,
    })),
    users: users.map((u) => ({
      id: u._id.toHexString(),
      name: u.name,
      unavailableWeekdays: u.unavailableWeekdays,
      dailyBudgetMinutes: u.dailyBudgetMinutes,
      maxDailyMinutes: u.maxDailyMinutes,
    })),
    ...(options.basePlan
      ? {
          currentSlots: options.basePlan.slots.map((s) => ({
            taskId: s.taskId.toHexString(),
            weekIndex: s.weekIndex,
            weekday: s.weekday,
            assigneeId: s.assigneeId?.toHexString() ?? null,
          })),
        }
      : {}),
    ...(options.constraints ? { constraints: options.constraints } : {}),
  };
  const outputSchema = buildPlanJsonSchema(
    basePayload.tasks.map((task) => task.id),
    basePayload.users.map((user) => user.id),
  );

  const tryOnce = async (previousErrors?: string[]): Promise<Attempt> => {
    const payload = { ...basePayload, ...(previousErrors ? { previousErrors } : {}) };
    const raw = await provider.completeJson({
      name: PLAN_PROPOSAL_REQUEST,
      schema: outputSchema,
      ...buildPlanPrompt(
        payload,
        outputSchema,
        settings.aiPromptTemplates
          ? undefined
          : options.mode === 'rebalance'
            ? (settings.aiPrompts?.planRebalance.trim() || DEFAULT_AI_PROMPTS.planRebalance)
            : (settings.aiPrompts?.planProposal.trim() || DEFAULT_AI_PROMPTS.planProposal),
        options.mode === 'rebalance'
          ? settings.aiPromptTemplates?.planRebalance
          : settings.aiPromptTemplates?.planProposal,
      ),
    });

    let json: unknown;
    try {
      json = JSON.parse(stripFence(raw));
    } catch {
      return { ok: false, errors: ['The answer was not valid JSON.'] };
    }
    const parsed = aiPlanOutputSchema.safeParse(json);
    if (!parsed.success) {
      return { ok: false, errors: parsed.error.issues.map((i) => `${i.path.join('.') || '(root)'}: ${i.message}`) };
    }

    const outside = parsed.data.slots
      .map((slot, index) => ({ slot, index }))
      .filter(({ slot }) => !allowedTaskIds.has(slot.taskId))
      .map(({ slot, index }) => `task_not_in_selection (slot ${index}, task ${slot.taskId})`);
    const proposedSlots: SlotDoc[] = parsed.data.slots.map((s) => ({
      taskId: new ObjectId(s.taskId),
      weekIndex: s.weekIndex,
      weekday: s.weekday,
      assigneeId: s.assigneeId === null ? null : new ObjectId(s.assigneeId),
      sortOrder: s.sortOrder ?? 0,
    }));
    const proposedValidation = await validateSlotsAgainstDb(ctx.db, proposedSlots);
    const errors = [...outside, ...proposedValidation.errors.map(describePlanError)];
    if (errors.length > 0) return { ok: false, errors };

    const completed = completeRequiredOccurrences(basePayload, parsed.data.slots);
    const unassigned = completed
      .map((slot, index) => ({ slot, index }))
      .filter(({ slot }) => slot.assigneeId === null)
      .map(({ slot, index }) => `assignee_required (slot ${index}, weekIndex ${slot.weekIndex}, weekday ${slot.weekday})`);
    if (unassigned.length > 0) return { ok: false, errors: unassigned };
    const slots: SlotDoc[] = completed.map((s) => ({
      taskId: new ObjectId(s.taskId),
      weekIndex: s.weekIndex,
      weekday: s.weekday,
      assigneeId: s.assigneeId === null ? null : new ObjectId(s.assigneeId),
      sortOrder: s.sortOrder ?? 0,
    }));
    const validation = await validateSlotsAgainstDb(ctx.db, slots);
    if (validation.errors.length > 0) return { ok: false, errors: validation.errors.map(describePlanError) };
    return { ok: true, slots, rationale: parsed.data.rationale, warnings: validation.warnings };
  };

  let attempt = await tryOnce();
  if (!attempt.ok) attempt = await tryOnce(attempt.errors);
  if (!attempt.ok) {
    throw new HttpError(422, 'ai_invalid_plan', 'The AI proposal did not pass validation', undefined, {
      errors: attempt.errors,
    });
  }

  const proposalId = randomUUID();
  const aiCtx: AuditContext = { ...ctx, source: 'ai' };
  const name =
    options.mode === 'rebalance' && options.basePlan
      ? `${options.basePlan.name} (herbalanceerd)`
      : `AI-voorstel ${new Date(ctx.clock.now()).toISOString().slice(0, 10)}`;
  const plan = await createPlan(
    aiCtx,
    {
      name,
      active: false,
      slots: attempt.slots,
      weekThemes: options.basePlan?.weekThemes ?? EMPTY_WEEK_THEMES,
      draft: true,
      source: 'ai',
      proposalId,
      rationale: attempt.rationale,
      discarded: false,
    },
    { proposalId, mode: options.mode, ...(options.basePlan ? { basePlanId: options.basePlan._id } : {}) },
  );
  return { plan, proposalId, warnings: attempt.warnings, rationale: attempt.rationale };
}

import {
  aiExplanationOutputSchema,
  aiTaskSuggestionsOutputSchema,
  DEFAULT_AI_PROMPTS,
  type TaskSuggestion,
} from '@huishoudplanner/shared';
import type { Db, ObjectId } from 'mongodb';
import type { z } from 'zod';
import { findPlanById, type WeekThemes } from '../../data/cyclePlans.ts';
import { findRoomById, listRooms } from '../../data/rooms.ts';
import { getSettings } from '../../data/settings.ts';
import { listTasks } from '../../data/tasks.ts';
import { listUsers } from '../../data/users.ts';
import { HttpError, notFound } from '../../http/errors.ts';
import type { AiProvider } from './provider.ts';
import {
  AI_EXPLANATION_JSON_SCHEMA,
  AI_TASK_SUGGESTIONS_JSON_SCHEMA,
  buildExplanationPrompt,
  buildTaskSuggestionsPrompt,
  PLAN_EXPLANATION_REQUEST,
  TASK_SUGGESTIONS_REQUEST,
} from './prompt.ts';

function stripFence(raw: string): string {
  const fenced = /^\s*```(?:json)?\s*([\s\S]*?)\s*```\s*$/i.exec(raw);
  return fenced ? fenced[1]! : raw;
}

/** Parses provider output against a schema; anything unusable becomes 422 `ai_invalid_response`. */
function parseOutput<S extends z.ZodType>(schema: S, raw: string): z.output<S> {
  let json: unknown;
  try {
    json = JSON.parse(stripFence(raw));
  } catch {
    throw new HttpError(422, 'ai_invalid_response', 'The AI answer was not valid JSON');
  }
  const parsed = schema.safeParse(json);
  if (!parsed.success) {
    throw new HttpError(422, 'ai_invalid_response', 'The AI answer did not match the expected format', undefined, {
      errors: parsed.error.issues.map((i) => `${i.path.join('.') || '(root)'}: ${i.message}`),
    });
  }
  return parsed.data;
}

const normalizeName = (name: string) => name.trim().toLocaleLowerCase('nl');

/**
 * Suggests missing tasks for a room. Nothing is stored. Suggestions with an
 * unknown interval key, an invalid duration, an empty name, or a name that
 * already exists in the room (or earlier in the answer) are dropped.
 */
export async function suggestTasks(db: Db, provider: AiProvider, roomId: ObjectId): Promise<TaskSuggestion[]> {
  const [settings, room, rooms, tasks] = await Promise.all([getSettings(db), findRoomById(db, roomId), listRooms(db), listTasks(db)]);
  if (!settings) throw new HttpError(500, 'settings_missing');
  if (!room) throw notFound('room');

  const roomName = new Map(rooms.map((r) => [r._id.toHexString(), r.name]));
  const inRoom = tasks.filter((t) => t.roomId.equals(roomId));
  const raw = await provider.completeJson({
    name: TASK_SUGGESTIONS_REQUEST,
    schema: AI_TASK_SUGGESTIONS_JSON_SCHEMA,
    ...buildTaskSuggestionsPrompt(
      {
        room: room.name,
        existingTasks: inRoom.map((t) => ({ name: t.name, intervalKey: t.intervalKey, durationMinutes: t.durationMinutes })),
        otherTasks: tasks
          .filter((t) => !t.roomId.equals(roomId) && t.active)
          .map((t) => ({ room: roomName.get(t.roomId.toHexString()) ?? null, name: t.name })),
        intervals: settings.intervals.map((i) => ({ key: i.key, label: i.label, periodDays: i.periodDays })),
      },
      settings.aiPromptTemplates ? undefined : (settings.aiPrompts?.taskSuggestions.trim() || DEFAULT_AI_PROMPTS.taskSuggestions),
      settings.aiPromptTemplates?.taskSuggestions,
    ),
  });
  const output = parseOutput(aiTaskSuggestionsOutputSchema, raw);

  const knownIntervals = new Set(settings.intervals.map((i) => i.key));
  const seen = new Set(inRoom.map((t) => normalizeName(t.name)));
  const result: TaskSuggestion[] = [];
  for (const suggestion of output.suggestions) {
    const name = suggestion.name.trim();
    const key = normalizeName(name);
    if (!name || seen.has(key)) continue;
    if (!knownIntervals.has(suggestion.intervalKey)) continue;
    if (!Number.isInteger(suggestion.durationMinutes) || suggestion.durationMinutes < 1) continue;
    seen.add(key);
    result.push({
      name,
      intervalKey: suggestion.intervalKey,
      durationMinutes: suggestion.durationMinutes,
      notes: suggestion.notes?.trim() ?? '',
    });
  }
  return result;
}

/** A short rationale per week for an existing plan. Nothing is stored. */
export async function explainPlan(db: Db, provider: AiProvider, planId: ObjectId): Promise<WeekThemes> {
  const plan = await findPlanById(db, planId);
  if (!plan) throw notFound('cycle plan');
  const [tasks, users, settings] = await Promise.all([listTasks(db), listUsers(db), getSettings(db)]);
  if (!settings) throw new HttpError(500, 'settings_missing');
  const taskById = new Map(tasks.map((t) => [t._id.toHexString(), t]));
  const userName = new Map(users.map((u) => [u._id.toHexString(), u.name]));

  const raw = await provider.completeJson({
    name: PLAN_EXPLANATION_REQUEST,
    schema: AI_EXPLANATION_JSON_SCHEMA,
    ...buildExplanationPrompt(
      {
        planName: plan.name,
        users: users
          .filter((u) => u.active)
          .map((u) => ({
            id: u._id.toHexString(),
            name: u.name,
            dailyBudgetMinutes: u.dailyBudgetMinutes,
            maxDailyMinutes: u.maxDailyMinutes,
          })),
        slots: plan.slots.map((s) => {
          const task = taskById.get(s.taskId.toHexString());
          return {
            task: task?.name ?? s.taskId.toHexString(),
            weekIndex: s.weekIndex,
            weekday: s.weekday,
            assignee: s.assigneeId ? (userName.get(s.assigneeId.toHexString()) ?? null) : null,
            durationMinutes: task?.durationMinutes ?? 0,
          };
        }),
      },
      settings.aiPromptTemplates ? undefined : (settings.aiPrompts?.planExplanation.trim() || DEFAULT_AI_PROMPTS.planExplanation),
      settings.aiPromptTemplates?.planExplanation,
    ),
  });
  return parseOutput(aiExplanationOutputSchema, raw).rationale;
}

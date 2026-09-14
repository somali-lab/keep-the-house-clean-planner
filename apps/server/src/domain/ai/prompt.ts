import {
  aiExplanationOutputSchema,
  aiPlanOutputSchema,
  aiTaskSuggestionsOutputSchema,
  type AiPromptTemplate,
  type AiPromptTemplates,
  type AiPrompts,
} from '@huishoudplanner/shared';
import { z } from 'zod';

export const PLAN_PROPOSAL_REQUEST = 'plan-proposal';
export const AI_CONNECTION_TEST_REQUEST = 'connection-test';
export const AI_CONNECTION_TEST_SCHEMA = {
  type: 'object',
  properties: { ok: { type: 'boolean', const: true } },
  required: ['ok'],
  additionalProperties: false,
} as const;

export interface PromptTask {
  id: string;
  name: string;
  room: string | null;
  intervalKey: string;
  intervalLabel: string;
  /** Required slots per 28-day cycle; null = optional (not planned via the grid). */
  perCycle: number | null;
  periodDays: number;
  durationMinutes: number;
}

export interface PromptUser {
  id: string;
  name: string;
  /** 0=Sunday..6=Saturday */
  unavailableWeekdays: number[];
  dailyBudgetMinutes: { weekday: number; weekend: number };
  maxDailyMinutes: { weekday: number; weekend: number };
}

export interface PromptSlot {
  taskId: string;
  weekIndex: number;
  weekday: number;
  assigneeId: string | null;
}

/** The user message is exactly this object as JSON, so it can be parsed back (e.g. by the mock). */
export interface PlanPromptPayload {
  mode: 'propose' | 'rebalance';
  tasks: PromptTask[];
  users: PromptUser[];
  currentSlots?: PromptSlot[];
  constraints?: string;
  /** Present on the single re-prompt: why the previous answer was rejected. */
  previousErrors?: string[];
}

export const AI_PLAN_JSON_SCHEMA = z.toJSONSchema(aiPlanOutputSchema) as Record<string, unknown>;

/** Narrow IDs to this household so structured-output providers cannot confuse task and user IDs. */
export function buildPlanJsonSchema(taskIds: string[], userIds: string[]): Record<string, unknown> {
  return {
    type: 'object',
    properties: {
      slots: {
        type: 'array',
        items: {
          type: 'object',
          properties: {
            taskId: { type: 'string', enum: taskIds },
            weekIndex: { type: 'integer', minimum: 0, maximum: 3 },
            weekday: { type: 'integer', minimum: 0, maximum: 6 },
            assigneeId: { type: 'string', enum: userIds },
            sortOrder: { type: 'integer' },
          },
          required: ['taskId', 'weekIndex', 'weekday', 'assigneeId'],
          additionalProperties: false,
        },
      },
      rationale: { type: 'array', items: { type: 'string', minLength: 1 }, minItems: 4, maxItems: 4 },
    },
    required: ['slots', 'rationale'],
    additionalProperties: false,
  };
}

export const SYSTEM_PROMPT = `You plan household chores for a household of the people listed in the input.
The plan is a repeating 4-week cycle. weekIndex is 0..3. weekday is 0=Sunday, 1=Monday … 6=Saturday.

Hard rules (a plan that breaks one is rejected):
- Only use task ids and user ids from the input.
- Never assign a slot to a user on a weekday listed in that user's unavailableWeekdays.
- Never place the same task twice on the same day (same weekIndex and weekday).
- Assign every slot to one concrete available user. Never use assigneeId null or an "either person" assignment.
- If one person is unavailable on a weekday, a shared/either-person assignment is not a workaround: assign the task to another available person or use another day.

Goals, in order:
1. Place each task exactly perCycle times; tasks with perCycle null are optional.
2. Keep each person's total minutes per week within two shared budgets: the weekday budget is the total for Monday–Friday together, and the weekend budget is the total for Saturday–Sunday together.
3. Keep each individual day's minutes within maxDailyMinutes: use weekday for Monday–Friday and weekend for Saturday–Sunday.
4. Balance total minutes per week between people.
5. Spread repeats of the same task evenly over the cycle.
6. Respect the free-text constraints (they are written in Dutch).
In "rebalance" mode, start from currentSlots and change only what improves fairness, spread or budgets.`;

function withCustomInstructions(system: string, customInstructions?: string): string {
  const custom = customInstructions?.trim();
  if (!custom) return system;
  return `${system}\n\nAction-specific instructions configured by the household. Follow these when they do not conflict with the hard rules or required JSON format:\n${custom}`;
}

function renderTemplate(template: string, token: '{{schema}}' | '{{input}}', value: unknown): string {
  const rendered = typeof value === 'string' ? value : JSON.stringify(value);
  return template.split(token).join(rendered);
}

function planTemplate(): AiPromptTemplate {
  return {
    system: `${SYSTEM_PROMPT}\n\nAnswer with one JSON object and nothing else, matching this JSON schema:\n{{schema}}\n"rationale" has exactly 4 short Dutch sentences, one per week, explaining the choices for that week.`,
    user: '{{input}}',
  };
}

export function buildPlanPrompt(
  payload: PlanPromptPayload,
  schema: Record<string, unknown> = AI_PLAN_JSON_SCHEMA,
  customInstructions?: string,
  template: AiPromptTemplate = planTemplate(),
): { system: string; user: string } {
  return {
    system: withCustomInstructions(renderTemplate(template.system, '{{schema}}', schema), customInstructions),
    user: renderTemplate(template.user, '{{input}}', payload),
  };
}

export const TASK_SUGGESTIONS_REQUEST = 'task-suggestions';
export const PLAN_EXPLANATION_REQUEST = 'plan-explanation';

export const AI_TASK_SUGGESTIONS_JSON_SCHEMA = z.toJSONSchema(aiTaskSuggestionsOutputSchema) as Record<string, unknown>;
export const AI_EXPLANATION_JSON_SCHEMA = z.toJSONSchema(aiExplanationOutputSchema) as Record<string, unknown>;

export interface TaskSuggestionPayload {
  room: string;
  /** Tasks already defined for this room (active or not), so they are not suggested again. */
  existingTasks: { name: string; intervalKey: string; durationMinutes: number }[];
  /** Tasks in other rooms, for context on how detailed tasks are. */
  otherTasks: { room: string | null; name: string }[];
  intervals: { key: string; label: string; periodDays: number }[];
}

export const TASK_SUGGESTIONS_SYSTEM = `You help a household keep a complete list of recurring chores.
Suggest chores for the given room that are missing from existingTasks.
Use only interval keys from "intervals". durationMinutes is a realistic whole number of minutes (at least 1).
Names and notes are in Dutch, short and concrete (e.g. "Afzuigkap ontvetten").
Answer with one JSON object and nothing else, matching this JSON schema:
{{schema}}`;

function taskSuggestionsTemplate(): AiPromptTemplate {
  return { system: TASK_SUGGESTIONS_SYSTEM, user: '{{input}}' };
}

export function buildTaskSuggestionsPrompt(
  payload: TaskSuggestionPayload,
  customInstructions?: string,
  template: AiPromptTemplate = taskSuggestionsTemplate(),
): { system: string; user: string } {
  return {
    system: withCustomInstructions(renderTemplate(template.system, '{{schema}}', AI_TASK_SUGGESTIONS_JSON_SCHEMA), customInstructions),
    user: renderTemplate(template.user, '{{input}}', payload),
  };
}

export interface ExplanationPayload {
  planName: string;
  users: {
    id: string;
    name: string;
    dailyBudgetMinutes: { weekday: number; weekend: number };
    maxDailyMinutes: { weekday: number; weekend: number };
  }[];
  slots: { task: string; weekIndex: number; weekday: number; assignee: string | null; durationMinutes: number }[];
}

export const EXPLANATION_SYSTEM = `You explain a 4-week household chore plan to the people who follow it.
weekIndex is 0..3, weekday is 0=Sunday … 6=Saturday, assignee null means "either person".
Write exactly 4 short Dutch sentences, one per week, about how that week is divided and why it is fair or where it is busy.
Answer with one JSON object and nothing else, matching this JSON schema:
{{schema}}`;

function explanationTemplate(): AiPromptTemplate {
  return { system: EXPLANATION_SYSTEM, user: '{{input}}' };
}

export function buildExplanationPrompt(
  payload: ExplanationPayload,
  customInstructions?: string,
  template: AiPromptTemplate = explanationTemplate(),
): { system: string; user: string } {
  return {
    system: withCustomInstructions(renderTemplate(template.system, '{{schema}}', AI_EXPLANATION_JSON_SCHEMA), customInstructions),
    user: renderTemplate(template.user, '{{input}}', payload),
  };
}

export function getDefaultAiPromptTemplates(): AiPromptTemplates {
  return {
    planProposal: planTemplate(),
    planRebalance: planTemplate(),
    taskSuggestions: taskSuggestionsTemplate(),
    planExplanation: explanationTemplate(),
  };
}

export function getAiPromptCodeInfo(legacy?: AiPrompts, configured?: AiPromptTemplates) {
  const defaults = getDefaultAiPromptTemplates();
  const effective =
    configured ??
    (Object.fromEntries(
      (Object.keys(defaults) as (keyof AiPromptTemplates)[]).map((key) => [
        key,
        {
          ...defaults[key],
          system: withCustomInstructions(defaults[key].system, legacy?.[key]),
        },
      ]),
    ) as AiPromptTemplates);
  const planOutput =
    'De code eist daarna één JSON-object met alle indelingen en precies vier Nederlandse toelichtingszinnen. Het JSON-schema wordt dynamisch beperkt tot de actuele taak- en persoon-id’s en vereist per taak een concrete uitvoerder.';
  return {
    actions: {
      planProposal: {
        ...effective.planProposal,
        fixedPrompt: effective.planProposal.system,
        dynamicData:
          'modus, alle actieve taken met ruimte/cyclus/frequentie/duur, alle actieve personen met niet-beschikbare dagen en minuutlimieten, vrije wensen en bij een tweede poging de validatiefouten. ' +
          planOutput,
      },
      planRebalance: {
        ...effective.planRebalance,
        fixedPrompt: effective.planRebalance.system,
        dynamicData: 'Dezelfde gegevens als bij een voorstel, plus alle huidige indelingen van het actieve plan. ' + planOutput,
      },
      taskSuggestions: {
        ...effective.taskSuggestions,
        fixedPrompt: effective.taskSuggestions.system,
        dynamicData: 'De gekozen ruimte, bestaande taken in die ruimte, actieve taken in andere ruimtes en alle toegestane cycli.',
      },
      planExplanation: {
        ...effective.planExplanation,
        fixedPrompt: effective.planExplanation.system,
        dynamicData: 'Plannaam, actieve personen met hun minuutlimieten en iedere indeling met taak, week, weekdag, uitvoerder en duur.',
      },
    },
    defaults,
  };
}

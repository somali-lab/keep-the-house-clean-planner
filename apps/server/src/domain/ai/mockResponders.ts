import type { AiPlanOutput } from '@huishoudplanner/shared';
import type { MockResponders } from './providers/mock.ts';
import {
  AI_CONNECTION_TEST_REQUEST,
  PLAN_EXPLANATION_REQUEST,
  PLAN_PROPOSAL_REQUEST,
  TASK_SUGGESTIONS_REQUEST,
  type ExplanationPayload,
  type PlanPromptPayload,
  type TaskSuggestionPayload,
} from './prompt.ts';

/** Monday-first order of 0=Sunday..6=Saturday weekdays. */
const WEEKDAYS = [1, 2, 3, 4, 5, 6, 0];

/**
 * A plan that always passes the hard rules: each task placed perCycle times,
 * evenly spread, never twice on a day, assigned to someone available that day
 * (rotating between people) or to "either person" when nobody is.
 */
export function deterministicPlan(payload: PlanPromptPayload): AiPlanOutput {
  const slots: AiPlanOutput['slots'] = [];
  payload.tasks.forEach((task, taskIndex) => {
    const count = Math.min(task.perCycle ?? 0, 28);
    const offset = (taskIndex * 3) % 28;
    for (let i = 0; i < count; i++) {
      const position = (Math.floor((i * 28) / count) + offset) % 28;
      const weekIndex = Math.floor(position / 7);
      const weekday = WEEKDAYS[position % 7]!;
      const people = payload.users.length;
      let assigneeId: string | null = null;
      for (let k = 0; k < people; k++) {
        const user = payload.users[(i + taskIndex + k) % people]!;
        if (!user.unavailableWeekdays.includes(weekday)) {
          assigneeId = user.id;
          break;
        }
      }
      slots.push({ taskId: task.id, weekIndex, weekday, assigneeId });
    }
  });
  return {
    slots,
    rationale: [
      'Week 1: taken zijn gelijkmatig verdeeld over de dagen.',
      'Week 2: niemand krijgt taken op een dag dat hij of zij niet kan.',
      'Week 3: herhalingen van dezelfde taak liggen ver uit elkaar.',
      'Week 4: de verdeling tussen de personen is zo gelijk mogelijk gehouden.',
    ],
  };
}

/**
 * Fixed suggestions, including one with an unknown interval key and one that
 * repeats an existing name, so the server-side filtering is exercised too.
 */
export function deterministicTaskSuggestions(payload: TaskSuggestionPayload) {
  const existing = payload.existingTasks[0]?.name;
  return {
    suggestions: [
      { name: `${payload.room}: plinten afnemen`, intervalKey: '4wk', durationMinutes: 15, notes: 'Vochtige doek.' },
      { name: `${payload.room}: lampen afstoffen`, intervalKey: 'quarter', durationMinutes: 10, notes: '' },
      { name: `${payload.room}: gordijnen wassen`, intervalKey: 'twice-a-year', durationMinutes: 60 },
      ...(existing ? [{ name: existing, intervalKey: '1w', durationMinutes: 20 }] : []),
    ],
  };
}

export function deterministicExplanation(payload: ExplanationPayload) {
  const perWeek = [0, 1, 2, 3].map((w) => payload.slots.filter((s) => s.weekIndex === w).length);
  return {
    rationale: perWeek.map((count, w) => `Week ${w + 1}: ${count} taken, verdeeld over de week.`) as [string, string, string, string],
  };
}

/** Used when settings select the mock provider outside tests (demo, E2E). */
export const defaultMockResponders: MockResponders = {
  [AI_CONNECTION_TEST_REQUEST]: ['{"ok":true}'],
  [PLAN_PROPOSAL_REQUEST]: (request) => JSON.stringify(deterministicPlan(JSON.parse(request.user) as PlanPromptPayload)),
  [TASK_SUGGESTIONS_REQUEST]: (request) =>
    JSON.stringify(deterministicTaskSuggestions(JSON.parse(request.user) as TaskSuggestionPayload)),
  [PLAN_EXPLANATION_REQUEST]: (request) =>
    JSON.stringify(deterministicExplanation(JSON.parse(request.user) as ExplanationPayload)),
};

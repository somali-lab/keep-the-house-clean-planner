import { z } from 'zod';
import { objectIdSchema, weekdaySchema, weekIndexSchema } from './common.ts';
import { aiProviderSettingsSchema } from './settings.ts';

export const aiConstraintsSchema = z.string().trim().max(2000);

export const proposePlanInputSchema = z.object({
  /** Omitted = all active tasks. */
  taskIds: z.array(objectIdSchema).min(1).optional(),
  constraints: aiConstraintsSchema.optional(),
});
export type ProposePlanInput = z.infer<typeof proposePlanInputSchema>;

export const rebalanceInputSchema = z.object({
  planId: objectIdSchema,
  constraints: aiConstraintsSchema.optional(),
});
export type RebalanceInput = z.infer<typeof rebalanceInputSchema>;

/** What the model must return: slots in the cyclePlans format plus one rationale per week. */
export const aiPlanOutputSchema = z.object({
  slots: z.array(
    z.object({
      taskId: objectIdSchema,
      weekIndex: weekIndexSchema,
      weekday: weekdaySchema,
      assigneeId: objectIdSchema.nullable(),
      sortOrder: z.number().int().optional(),
    }),
  ),
  rationale: z.tuple([z.string().min(1), z.string().min(1), z.string().min(1), z.string().min(1)]),
});
export type AiPlanOutput = z.infer<typeof aiPlanOutputSchema>;

export const suggestTasksInputSchema = z.object({ roomId: objectIdSchema });
export type SuggestTasksInput = z.infer<typeof suggestTasksInputSchema>;

export const explainPlanInputSchema = z.object({ planId: objectIdSchema });
export type ExplainPlanInput = z.infer<typeof explainPlanInputSchema>;

export const testAiProviderInputSchema = z.object({ aiProvider: aiProviderSettingsSchema });
export type TestAiProviderInput = z.infer<typeof testAiProviderInputSchema>;

/** Raw model output; the server filters it before returning. */
export const aiTaskSuggestionsOutputSchema = z.object({
  suggestions: z.array(
    z.object({
      name: z.string(),
      intervalKey: z.string(),
      durationMinutes: z.number(),
      notes: z.string().optional(),
    }),
  ),
});

export interface TaskSuggestion {
  name: string;
  intervalKey: string;
  durationMinutes: number;
  notes: string;
}

export const aiExplanationOutputSchema = z.object({
  rationale: z.tuple([z.string().min(1), z.string().min(1), z.string().min(1), z.string().min(1)]),
});

export interface AiProposalResponse {
  planId: string;
  proposalId: string;
  warnings: { code: string; [key: string]: unknown }[];
  rationale: [string, string, string, string];
}

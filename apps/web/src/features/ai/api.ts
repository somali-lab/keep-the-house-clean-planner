import { useMutation, useMutationState, useQueryClient } from '@tanstack/react-query';
import { apiV2, unwrap } from '../../api/index.ts';
import { toInt } from '../../api/occurrence.ts';
import type { components } from '../../api/v2/schema';
import { toIssue, planKeys, type PlanIssue } from '../planner/api.ts';

type Schemas = components['schemas'];

const aiGenerationKey = ['ai', 'generation'] as const;

/** A stored AI draft: the plan is an inactive concept, `warnings` are the non-blocking issues of its validation. */
export interface AiProposal {
  planId: string;
  proposalId: string;
  warnings: PlanIssue[];
  /** Four sentences, one per week. */
  rationale: string[];
}

/** A task the AI suggests for a room. */
export interface TaskSuggestion {
  name: string;
  intervalKey: string;
  durationMinutes: number;
  notes: string;
}

/** Keeps the running AI request visible even when the AI page is unmounted during navigation. */
export function useAiGenerationStartedAt(): number | null {
  const pending = useMutationState({
    filters: { mutationKey: aiGenerationKey, status: 'pending' },
    select: (mutation) => mutation.state.submittedAt,
  });
  return pending.length > 0 ? Math.min(...pending) : null;
}

export function useAiActions() {
  const queryClient = useQueryClient();
  const refreshPlans = () => queryClient.invalidateQueries({ queryKey: planKeys.all });

  const propose = useMutation({
    mutationKey: [...aiGenerationKey, 'propose'],
    // `taskIds` is left out (all active tasks) and so are empty constraints: the parser refuses an explicit null.
    mutationFn: async (input: { constraints?: string }): Promise<AiProposal> => {
      const { data } = await unwrap(
        apiV2.POST('/api/v2/ai/propose-plan', { body: (input.constraints ? { constraints: input.constraints } : {}) as Schemas['ProposePlanRequest'] }),
      );
      return { ...data, warnings: data.warnings.map(toIssue) };
    },
    onSuccess: refreshPlans,
  });

  const rebalance = useMutation({
    mutationKey: [...aiGenerationKey, 'rebalance'],
    mutationFn: async (input: { planId: string; constraints?: string }): Promise<AiProposal> => {
      const body = input.constraints ? { planId: input.planId, constraints: input.constraints } : { planId: input.planId };
      const { data } = await unwrap(apiV2.POST('/api/v2/ai/rebalance', { body: body as Schemas['RebalancePlanRequest'] }));
      return { ...data, warnings: data.warnings.map(toIssue) };
    },
    onSuccess: refreshPlans,
  });

  const suggestTasks = useMutation({
    mutationKey: [...aiGenerationKey, 'suggest-tasks'],
    mutationFn: async (roomId: string): Promise<TaskSuggestion[]> =>
      (await unwrap(apiV2.POST('/api/v2/ai/suggest-tasks', { body: { roomId } }))).data.suggestions.map((s) => ({
        ...s,
        durationMinutes: toInt(s.durationMinutes),
      })),
  });

  const explain = useMutation({
    mutationKey: [...aiGenerationKey, 'explain'],
    mutationFn: async (planId: string): Promise<string[]> =>
      (await unwrap(apiV2.POST('/api/v2/ai/explain', { body: { planId } }))).data.rationale,
  });

  return { propose, rebalance, suggestTasks, explain };
}

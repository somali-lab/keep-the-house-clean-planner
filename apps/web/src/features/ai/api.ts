import type { AiProposalResponse, TaskSuggestion } from '@huishoudplanner/shared';
import { useMutation, useMutationState, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/index.ts';
import { planKeys } from '../planner/api.ts';

const aiGenerationKey = ['ai', 'generation'] as const;

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
    mutationFn: async (input: { constraints?: string }) =>
      (await api.post<AiProposalResponse>('/api/ai/propose-plan', input)).data,
    onSuccess: refreshPlans,
  });

  const rebalance = useMutation({
    mutationKey: [...aiGenerationKey, 'rebalance'],
    mutationFn: async (input: { planId: string; constraints?: string }) =>
      (await api.post<AiProposalResponse>('/api/ai/rebalance', input)).data,
    onSuccess: refreshPlans,
  });

  const suggestTasks = useMutation({
    mutationKey: [...aiGenerationKey, 'suggest-tasks'],
    mutationFn: async (roomId: string) =>
      (await api.post<{ suggestions: TaskSuggestion[] }>('/api/ai/suggest-tasks', { roomId })).data.suggestions,
  });

  const explain = useMutation({
    mutationKey: [...aiGenerationKey, 'explain'],
    mutationFn: async (planId: string) =>
      (await api.post<{ rationale: string[] }>('/api/ai/explain', { planId })).data.rationale,
  });

  return { propose, rebalance, suggestTasks, explain };
}

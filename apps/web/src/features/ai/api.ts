import type { AiProposalResponse, TaskSuggestion, WeekSummary } from '@huishoudplanner/shared';
import { useMutation, useMutationState, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/index.ts';
import { planKeys } from '../planner/api.ts';

export interface DiffPosition {
  weekIndex: number;
  weekday: number;
  assigneeId: string | null;
}

export interface DiffSlot extends DiffPosition {
  taskId: string;
  taskName: string;
  roomName: string | null;
  durationMinutes: number;
}

export interface MovedSlot {
  taskId: string;
  taskName: string;
  roomName: string | null;
  durationMinutes: number;
  from: DiffPosition;
  to: DiffPosition;
}

export interface PlanDiffResponse {
  planId: string;
  againstPlanId: string | null;
  added: DiffSlot[];
  removed: DiffSlot[];
  moved: MovedSlot[];
  unchanged: number;
  summary: { before: WeekSummary[]; after: WeekSummary[] };
  warnings: { code: string; taskId?: string; placed?: number; required?: number }[];
}

const aiGenerationKey = ['ai', 'generation'] as const;

/** Keeps the running AI request visible even when the AI page is unmounted during navigation. */
export function useAiGenerationStartedAt(): number | null {
  const pending = useMutationState({
    filters: { mutationKey: aiGenerationKey, status: 'pending' },
    select: (mutation) => mutation.state.submittedAt,
  });
  return pending.length > 0 ? Math.min(...pending) : null;
}

export function usePlanDiff(planId: string | null) {
  return useQuery({
    queryKey: ['plan-diff', planId],
    queryFn: async () => (await api.get<PlanDiffResponse>(`/api/cycle-plans/${planId}/diff?against=active`)).data,
    enabled: planId !== null,
  });
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

  const apply = useMutation({
    mutationFn: async (planId: string) => api.post(`/api/cycle-plans/${planId}/apply-proposal`),
    onSuccess: () =>
      Promise.all([
        refreshPlans(),
        queryClient.invalidateQueries({ queryKey: ['occurrences'] }),
        queryClient.invalidateQueries({ queryKey: ['due'] }),
      ]),
  });

  const discard = useMutation({
    mutationFn: async (planId: string) => api.post(`/api/cycle-plans/${planId}/discard`),
    onSuccess: refreshPlans,
  });

  return { propose, rebalance, suggestTasks, explain, apply, discard };
}

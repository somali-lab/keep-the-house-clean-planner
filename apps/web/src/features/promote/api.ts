import type { PromoteSuggestion } from '@huishoudplanner/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/index.ts';

export const promoteKeys = { all: ['promote-suggestions'] as const };

export function usePromoteSuggestions() {
  return useQuery({
    queryKey: promoteKeys.all,
    queryFn: async () => (await api.get<PromoteSuggestion[]>('/api/promote-suggestions')).data,
  });
}

export function usePromoteActions() {
  const queryClient = useQueryClient();
  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: promoteKeys.all }),
      queryClient.invalidateQueries({ queryKey: ['cycle-plans'] }),
    ]);

  const apply = useMutation({
    mutationFn: async (s: PromoteSuggestion) =>
      api.post('/api/promote-suggestions/apply', {
        planId: s.planId,
        taskId: s.taskId,
        weekIndex: s.fromSlot.weekIndex,
        weekday: s.fromSlot.weekday,
        toWeekday: s.toWeekday,
        ...(s.toAssigneeId ? { toAssigneeId: s.toAssigneeId } : {}),
      }),
    onSuccess: refresh,
  });

  const dismiss = useMutation({
    mutationFn: async (s: PromoteSuggestion) =>
      api.post('/api/promote-suggestions/dismiss', {
        planId: s.planId,
        taskId: s.taskId,
        weekIndex: s.fromSlot.weekIndex,
        weekday: s.fromSlot.weekday,
        toWeekday: s.toWeekday,
        toAssigneeId: s.toAssigneeId ?? null,
        lastEvidenceId: s.evidence[0],
      }),
    onSuccess: refresh,
  });

  return { apply, dismiss };
}

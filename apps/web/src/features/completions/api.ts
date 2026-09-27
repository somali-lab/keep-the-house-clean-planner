import type { OccurrenceView } from '@huishoudplanner/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/index.ts';

export interface CompletionEdit {
  id: string;
  date: string;
  completedAt: string;
  completedBy: string;
}

export function useCompletionRecords(from: string, to: string) {
  return useQuery({
    queryKey: ['occurrences', 'completed', from, to],
    queryFn: async () =>
      (
        await api.get<OccurrenceView[]>(
          `/api/occurrences?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}&status=done`,
        )
      ).data,
  });
}

function useRefreshCompletionData() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries();
}

export function useEditCompletion() {
  const refresh = useRefreshCompletionData();
  return useMutation({
    mutationFn: async ({ id, ...input }: CompletionEdit) =>
      (
        await api.patch<OccurrenceView>(`/api/occurrences/${id}`, {
          action: 'edit_completion',
          ...input,
        })
      ).data,
    onSuccess: refresh,
  });
}

export function useDeleteCompletion() {
  const refresh = useRefreshCompletionData();
  return useMutation({
    mutationFn: async (id: string) => (await api.delete<{ deleted: true }>(`/api/occurrences/${id}`)).data,
    onSuccess: refresh,
  });
}
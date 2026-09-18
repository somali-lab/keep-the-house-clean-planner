import type { CompletionResponse, DeviationsResponse, IntervalsResponse, StatsGroupBy, WorkloadResponse } from '@huishoudplanner/shared';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/index.ts';

// keepPreviousData: a refetch holds the previous render instead of flashing a loader.

export function useWorkload(weeks: number) {
  return useQuery({
    queryKey: ['stats', 'workload', 'weeks', weeks],
    queryFn: async () => (await api.get<WorkloadResponse>(`/api/stats/workload?weeks=${weeks}`)).data,
    placeholderData: keepPreviousData,
  });
}

export function useCompletion(weeks: number, groupBy: StatsGroupBy) {
  return useQuery({
    queryKey: ['stats', 'completion', 'weeks', weeks, groupBy],
    queryFn: async () =>
      (await api.get<CompletionResponse>(`/api/stats/completion?weeks=${weeks}&groupBy=${groupBy}`)).data,
    placeholderData: keepPreviousData,
  });
}

export function useIntervals(weeks: number) {
  return useQuery({
    queryKey: ['stats', 'intervals', 'weeks', weeks],
    queryFn: async () => (await api.get<IntervalsResponse>(`/api/stats/intervals?weeks=${weeks}`)).data,
    placeholderData: keepPreviousData,
  });
}

export function useDeviations(weeks: number) {
  return useQuery({
    queryKey: ['stats', 'deviations', 'weeks', weeks],
    queryFn: async () => (await api.get<DeviationsResponse>(`/api/stats/deviations?weeks=${weeks}`)).data,
    placeholderData: keepPreviousData,
  });
}

export function useResetStatistics() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async () => api.delete('/api/stats'),
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: ['stats'] }),
        queryClient.invalidateQueries({ queryKey: ['occurrences'] }),
        queryClient.invalidateQueries({ queryKey: ['due'] }),
        queryClient.invalidateQueries({ queryKey: ['tasks'] }),
      ]),
  });
}

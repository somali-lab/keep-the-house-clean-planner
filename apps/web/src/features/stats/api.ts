import type { CompletionResponse, DeviationsResponse, IntervalsResponse, StatsGroupBy, WorkloadResponse } from '@huishoudplanner/shared';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/index.ts';

// keepPreviousData: a refetch holds the previous render instead of flashing a loader.

export function useWorkload(cycles: number) {
  return useQuery({
    queryKey: ['stats', 'workload', cycles],
    queryFn: async () => (await api.get<WorkloadResponse>(`/api/stats/workload?cycles=${cycles}`)).data,
    placeholderData: keepPreviousData,
  });
}

export function useCompletion(cycles: number, groupBy: StatsGroupBy) {
  return useQuery({
    queryKey: ['stats', 'completion', cycles, groupBy],
    queryFn: async () =>
      (await api.get<CompletionResponse>(`/api/stats/completion?cycles=${cycles}&groupBy=${groupBy}`)).data,
    placeholderData: keepPreviousData,
  });
}

export function useIntervals(cycles: number) {
  return useQuery({
    queryKey: ['stats', 'intervals', cycles],
    queryFn: async () => (await api.get<IntervalsResponse>(`/api/stats/intervals?cycles=${cycles}`)).data,
    placeholderData: keepPreviousData,
  });
}

export function useDeviations(cycles: number) {
  return useQuery({
    queryKey: ['stats', 'deviations', cycles],
    queryFn: async () => (await api.get<DeviationsResponse>(`/api/stats/deviations?cycles=${cycles}`)).data,
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

import type { CompletionResponse, DeviationsResponse, IntervalsResponse, StatsGroupBy, WorkloadResponse } from '@huishoudplanner/shared';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/index.ts';

// keepPreviousData: a refetch holds the previous render instead of flashing a loader.

export interface StatsPeriod {
  unit: 'weeks' | 'cycles';
  count: number;
}

const periodQuery = ({ unit, count }: StatsPeriod) => `${unit}=${count}`;

export function useWorkload(period: StatsPeriod) {
  return useQuery({
    queryKey: ['stats', 'workload', period.unit, period.count],
    queryFn: async () => (await api.get<WorkloadResponse>(`/api/stats/workload?${periodQuery(period)}`)).data,
    placeholderData: keepPreviousData,
  });
}

export function useCompletion(period: StatsPeriod, groupBy: StatsGroupBy) {
  return useQuery({
    queryKey: ['stats', 'completion', period.unit, period.count, groupBy],
    queryFn: async () =>
      (await api.get<CompletionResponse>(`/api/stats/completion?${periodQuery(period)}&groupBy=${groupBy}`)).data,
    placeholderData: keepPreviousData,
  });
}

export function useIntervals(period: StatsPeriod) {
  return useQuery({
    queryKey: ['stats', 'intervals', period.unit, period.count],
    queryFn: async () => (await api.get<IntervalsResponse>(`/api/stats/intervals?${periodQuery(period)}`)).data,
    placeholderData: keepPreviousData,
  });
}

export function useDeviations(period: StatsPeriod) {
  return useQuery({
    queryKey: ['stats', 'deviations', period.unit, period.count],
    queryFn: async () => (await api.get<DeviationsResponse>(`/api/stats/deviations?${periodQuery(period)}`)).data,
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

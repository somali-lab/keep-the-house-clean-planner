import type {
  CompletionResponse,
  DeviationsResponse,
  IntervalsResponse,
  PointsBalancesResponse,
  PointsEntriesResponse,
  StatsGroupBy,
  WorkloadResponse,
} from '@huishoudplanner/shared';
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

export function usePointsBalances(range: { from: string; to: string } | null) {
  return useQuery({
    queryKey: ['points', 'balances', range?.from, range?.to],
    queryFn: async () =>
      (await api.get<PointsBalancesResponse>(`/api/points/balances?from=${range!.from}&to=${range!.to}`)).data,
    enabled: range !== null,
    placeholderData: keepPreviousData,
  });
}

export function usePointsEntries(personId: string | null, range: { from: string; to: string } | null) {
  return useQuery({
    queryKey: ['points', 'entries', personId, range?.from, range?.to],
    queryFn: async () =>
      (
        await api.get<PointsEntriesResponse>(
          `/api/points/entries?personId=${personId}&from=${range!.from}&to=${range!.to}`,
        )
      ).data,
    enabled: personId !== null && range !== null,
    placeholderData: keepPreviousData,
  });
}

export function useResetStatistics() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (before?: string) => api.delete(`/api/stats${before ? `?before=${before}` : ''}`),
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: ['stats'] }),
        queryClient.invalidateQueries({ queryKey: ['points'] }),
        queryClient.invalidateQueries({ queryKey: ['occurrences'] }),
        queryClient.invalidateQueries({ queryKey: ['due'] }),
        queryClient.invalidateQueries({ queryKey: ['tasks'] }),
      ]),
  });
}

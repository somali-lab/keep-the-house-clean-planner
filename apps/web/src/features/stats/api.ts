import type {
  CompletionResponse,
  DeviationsResponse,
  IntervalsResponse,
  PointEntryView,
  PointsBalancesResponse,
  PointsEntriesResponse,
  StatsGroupBy,
  WorkloadResponse,
} from '@huishoudplanner/shared';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/index.ts';
import { releaseRequestKey, requestKeyFor } from '../../api/requestKey.ts';

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

/** Balances over the whole ledger, without a range: what a person can redeem right now (requirements 4.12). */
export function useAllTimeBalances(enabled = true) {
  return useQuery({
    queryKey: ['points', 'balances', 'all'],
    queryFn: async () => (await api.get<PointsBalancesResponse>('/api/points/balances')).data,
    enabled,
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

export interface RedeemInput {
  personId: string;
  points: number;
  note: string;
}

/**
 * Books a redemption (requirements 4.12). The request key belongs to the intent (person, points and note): a repeated
 * click, a retry after a failure, or a closed and reopened dialog with the same values reuses it, so the
 * server books it once; it is dropped once the request succeeded. Not queued offline: the server decides
 * whether the balance is enough.
 */
export function useRedeemPoints() {
  const queryClient = useQueryClient();
  return useMutation({
    networkMode: 'always',
    mutationFn: async (input: RedeemInput): Promise<{ entry: PointEntryView; replayed: boolean }> => {
      const intent = `redeem:${JSON.stringify(input)}`;
      const requestId = requestKeyFor(intent);
      const { data, status } = await api.post<PointEntryView>('/api/points/redemptions', {
        personId: input.personId,
        points: input.points,
        ...(input.note ? { note: input.note } : {}),
        requestId,
      });
      releaseRequestKey(intent);
      // 201 is a new booking; 200 means the server already had this request and replayed it.
      return { entry: data, replayed: status === 200 };
    },
    // The balance may also have changed under us (insufficient_balance), so refetch whatever the outcome.
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['points'] }),
  });
}

/** Takes a redemption back: the owner on the day it was booked, an administrator at any time. */
export function useUndoRedemption() {
  const queryClient = useQueryClient();
  return useMutation({
    networkMode: 'always',
    mutationFn: async (id: string) => api.delete(`/api/points/redemptions/${id}`),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['points'] }),
  });
}

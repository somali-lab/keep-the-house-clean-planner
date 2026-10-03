import type { PointsProgressResponse, RewardPeriod } from '@huishoudplanner/shared';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/index.ts';

/** How often an open reward tab reads the progress again. */
export const PROGRESS_REFETCH_MS = 5 * 60 * 1000;

/** Under the `points` prefix, so everything that changes points (a redemption, a reset, the conversion) refreshes the meter too. */
export const progressKey = (personId: string | null, period: RewardPeriod) => ['points', 'progress', personId, period] as const;

/** How far one person is towards the goal of this week or cycle (ADR-0015). */
export function usePointsProgress(personId: string | null, period: RewardPeriod) {
  return useQuery({
    queryKey: progressKey(personId, period),
    queryFn: async () =>
      (await api.get<PointsProgressResponse>(`/api/points/progress?personId=${personId}&period=${period}`)).data,
    enabled: personId !== null,
    // The meter is read again whenever the tab is looked at, and every few minutes while it stays open; a check-off on
    // another device or a new week is then picked up without a reload.
    refetchOnWindowFocus: 'always',
    refetchInterval: PROGRESS_REFETCH_MS,
  });
}

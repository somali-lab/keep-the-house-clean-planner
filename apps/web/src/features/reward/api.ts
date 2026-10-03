import type { PointsProgressResponse, RewardPeriod } from '@huishoudplanner/shared';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/index.ts';

/** Under the `points` prefix, so everything that changes points (a redemption, a reset, the conversion) refreshes the meter too. */
export const progressKey = (personId: string | null, period: RewardPeriod) => ['points', 'progress', personId, period] as const;

/** How far one person is towards the goal of this week or cycle (ADR-0015). */
export function usePointsProgress(personId: string | null, period: RewardPeriod) {
  return useQuery({
    queryKey: progressKey(personId, period),
    queryFn: async () =>
      (await api.get<PointsProgressResponse>(`/api/points/progress?personId=${personId}&period=${period}`)).data,
    enabled: personId !== null,
  });
}

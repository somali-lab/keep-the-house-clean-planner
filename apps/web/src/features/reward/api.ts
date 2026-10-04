import { useQuery } from '@tanstack/react-query';
import { apiV2, unwrap } from '../../api/index.ts';
import { toInt } from '../../api/occurrence.ts';

export type RewardPeriod = 'week' | 'cycle';

/** The money of a progress, in whole cents; `goal` is null while the period has no goal. */
export interface RewardMoney {
  earned: number;
  goal: number | null;
}

/** How far one person is towards the goal of this week or cycle, as `GET /api/v2/points/progress` answers it (requirements 4.12). */
export interface RewardProgress {
  personId: string;
  period: RewardPeriod;
  start: string;
  end: string;
  earnedPoints: number;
  /** Null when the period has no goal. */
  goalPoints: number | null;
  goalSource: 'explicit' | 'automatic';
  percent: number;
  /** The eggs in the basket and how many places it has: both counted by the server. */
  eggs: number;
  eggCount: number;
  currencyCode: string;
  centsPerPoint: number;
  /** Null while a point is worth nothing. */
  money: RewardMoney | null;
}

/** How often an open reward tab reads the progress again. */
export const PROGRESS_REFETCH_MS = 5 * 60 * 1000;

/** Under the `points` prefix, so everything that changes points (a redemption, a reset, the conversion) refreshes the meter too. */
export const progressKey = (personId: string | null, period: RewardPeriod) => ['points', 'progress', personId, period] as const;

/** How far one person is towards the goal of this week or cycle (requirements 4.12). */
export function usePointsProgress(personId: string | null, period: RewardPeriod) {
  return useQuery({
    queryKey: progressKey(personId, period),
    queryFn: async (): Promise<RewardProgress> => {
      const { data } = await unwrap(apiV2.GET('/api/v2/points/progress', { params: { query: { personId: personId!, period } } }));
      return {
        personId: data.personId,
        period: data.period as RewardPeriod,
        start: data.start,
        end: data.end,
        earnedPoints: toInt(data.earnedPoints),
        goalPoints: data.goalPoints === null ? null : toInt(data.goalPoints),
        goalSource: data.goalSource as RewardProgress['goalSource'],
        percent: toInt(data.percent),
        eggs: toInt(data.eggs),
        eggCount: toInt(data.eggCount),
        currencyCode: data.currencyCode,
        centsPerPoint: toInt(data.centsPerPoint),
        money: data.money ? { earned: toInt(data.money.earned), goal: data.money.goal === null ? null : toInt(data.money.goal) } : null,
      };
    },
    enabled: personId !== null,
    // The meter is read again whenever the tab is looked at, and every few minutes while it stays open; a check-off on
    // another device or a new week is then picked up without a reload.
    refetchOnWindowFocus: 'always',
    refetchInterval: PROGRESS_REFETCH_MS,
  });
}

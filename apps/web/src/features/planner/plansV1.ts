import type { CyclePlan } from '@huishoudplanner/shared';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/index.ts';

/**
 * The plans as the Node server (`/api/cycle-plans`) lists them. The planner itself is on `/api/v2`; Distribution and History (slice 7.5)
 * still read this shape, and go to the v2 list with their slice. The key is under `['cycle-plans']`, as the invalidations expect.
 */
export const plansV1Key = ['cycle-plans'] as const;

export function usePlansV1() {
  return useQuery({
    queryKey: plansV1Key,
    queryFn: async () => (await api.get<CyclePlan[]>('/api/cycle-plans')).data,
  });
}

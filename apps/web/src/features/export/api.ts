import type { Cycle } from '@huishoudplanner/shared';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/index.ts';

export function useCycles() {
  return useQuery({
    queryKey: ['cycles'],
    queryFn: async () => (await api.get<Cycle[]>('/api/cycles')).data,
  });
}

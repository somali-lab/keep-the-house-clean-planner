import type { Room, Task } from '@huishoudplanner/shared';
import { useQuery } from '@tanstack/react-query';
import { api } from './index.ts';

/**
 * Query keys of the Node client that features still share, so mutations can invalidate the right data. The people and the
 * settings are read from `/api/v2` (`v2/household.ts`); rooms and tasks of the features that have not moved yet stay here.
 */
export const queryKeys = {
  rooms: ['rooms'] as const,
  tasks: ['tasks'] as const,
};

export function useRooms() {
  return useQuery({
    queryKey: queryKeys.rooms,
    queryFn: async () => (await api.get<Room[]>('/api/rooms')).data,
  });
}

export function useTasks() {
  return useQuery({
    queryKey: queryKeys.tasks,
    queryFn: async () => (await api.get<Task[]>('/api/tasks')).data,
  });
}

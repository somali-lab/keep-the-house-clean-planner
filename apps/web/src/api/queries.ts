import type { Room, Settings, Task, User } from '@huishoudplanner/shared';
import { useQuery } from '@tanstack/react-query';
import { api } from './index.ts';

/** Query keys shared across features, so mutations can invalidate the right data. */
export const queryKeys = {
  rooms: ['rooms'] as const,
  tasks: ['tasks'] as const,
  settings: ['settings'] as const,
};

/** All users, including inactive ones (history still names them). Shares its cache with the profile context. */
export function useUsers() {
  return useQuery({
    queryKey: ['users'],
    queryFn: async () => (await api.get<User[]>('/api/users')).data,
  });
}

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

export function useSettings() {
  return useQuery({
    queryKey: queryKeys.settings,
    queryFn: async () => (await api.get<Settings>('/api/settings')).data,
  });
}

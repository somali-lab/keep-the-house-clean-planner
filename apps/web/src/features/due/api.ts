import type { OccurrenceView } from '@huishoudplanner/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/index.ts';

/** JSON shape of GET /api/due items. */
export interface DueItemView {
  taskId: string;
  taskName: string;
  roomId: string;
  roomName: string | null;
  intervalKey: string;
  intervalLabel: string;
  periodDays: number;
  daysSince: number;
  ratio: number;
  state: 'ok' | 'due' | 'overdue';
  lastCompletedAt: string | null;
  nextOccurrence: { id: string; date: string; assigneeId: string | null } | null;
}

export const dueKeys = { all: ['due'] as const };

export function useDue() {
  return useQuery({
    queryKey: dueKeys.all,
    queryFn: async () => (await api.get<DueItemView[]>('/api/due')).data,
  });
}

/** "Inplannen" and "Nu gedaan" both refresh the due list and any loaded day lists. */
export function useDueActions() {
  const queryClient = useQueryClient();
  const refresh = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: dueKeys.all }),
      queryClient.invalidateQueries({ queryKey: ['occurrences'] }),
    ]);
  };

  const plan = useMutation({
    mutationFn: async (input: { taskId: string; date: string; assigneeId: string | null }) =>
      (await api.post<OccurrenceView>('/api/occurrences', input)).data,
    onSuccess: refresh,
  });

  const doneNow = useMutation({
    mutationFn: async (input: { item: DueItemView; todayKey: string; profileId: string }) => {
      // Complete today's planned occurrence if there is one; otherwise add one for today and complete it.
      const plannedToday = input.item.nextOccurrence?.date === input.todayKey ? input.item.nextOccurrence.id : null;
      const id =
        plannedToday ??
        (
          await api.post<OccurrenceView>('/api/occurrences', {
            taskId: input.item.taskId,
            date: input.todayKey,
            assigneeId: input.profileId,
          })
        ).data._id;
      return (await api.patch<OccurrenceView>(`/api/occurrences/${id}`, { action: 'complete' })).data;
    },
    onSuccess: refresh,
  });

  return { plan, doneNow };
}

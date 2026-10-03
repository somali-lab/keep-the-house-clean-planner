import type { OccurrenceView } from '@huishoudplanner/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/index.ts';
import { releaseRequestKey, requestKeyFor } from '../../api/requestKey.ts';

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
  initialDueDate: string;
  nextOccurrence: { id: string; date: string; assigneeId: string | null } | null;
}

/** Who performed today's occurrence of someone else (ADR-0011): a named person, or the actor taking it over. */
export type DoneNowChoice = { completedBy: string } | { takeOver: true };

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
    mutationFn: async (input: { item: DueItemView; todayKey: string; choice?: DoneNowChoice }) => {
      // Today's planned occurrence is completed in place; otherwise the extra execution is recorded
      // as done in one request. The key makes a retry of the same click idempotent (ADR-0009).
      if (input.item.nextOccurrence?.date === input.todayKey) {
        return (
          await api.patch<OccurrenceView>(`/api/occurrences/${input.item.nextOccurrence.id}`, {
            action: 'complete',
            ...input.choice,
          })
        ).data;
      }
      // One key per intent, kept across retries and page changes until the request succeeded.
      const intent = `done-now:${input.item.taskId}:${input.todayKey}`;
      const created = (
        await api.post<OccurrenceView>('/api/occurrences', {
          taskId: input.item.taskId,
          date: input.todayKey,
          done: true,
          requestId: requestKeyFor(intent),
        })
      ).data;
      releaseRequestKey(intent);
      return created;
    },
    onSuccess: refresh,
  });

  return { plan, doneNow };
}

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiV2, toOccurrence, unwrap, type Occurrence } from '../../api/index.ts';
import { toInt } from '../../api/occurrence.ts';
import { releaseRequestKey, requestKeyFor } from '../../api/requestKey.ts';
import { collectPages } from '../../api/v2/paging.ts';
import { sendOccurrenceAction } from '../today/api.ts';

/** One task of `GET /api/v2/due`. */
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

/** The most the server returns in one page of the due list (`limit` 1 to 200). */
const PAGE_SIZE = 200;

export async function fetchDue(): Promise<DueItemView[]> {
  const items = await collectPages(async (cursor) =>
    (await unwrap(apiV2.GET('/api/v2/due', { params: { query: { limit: String(PAGE_SIZE), cursor } } }))).data,
  );
  return items.map((item) => ({
    ...item,
    periodDays: toInt(item.periodDays),
    daysSince: toInt(item.daysSince),
    ratio: Number(item.ratio),
    state: item.state as DueItemView['state'],
  }));
}

export function useDue() {
  return useQuery({ queryKey: dueKeys.all, queryFn: fetchDue });
}

/** "Inplannen" and "Nu gedaan" both refresh the due list and any loaded day lists. */
export function useDueActions() {
  const queryClient = useQueryClient();
  const refresh = async () => {
    await Promise.all(
      ['due', 'occurrences', 'tasks', 'stats', 'badges'].map((key) => queryClient.invalidateQueries({ queryKey: [key] })),
    );
  };

  const plan = useMutation({
    mutationFn: async (input: { taskId: string; date: string; assigneeId: string | null }): Promise<Occurrence> => {
      // `assigneeId: null` is the documented "anyone"; `done` and `requestId` are left out.
      const { data } = await unwrap(
        apiV2.POST('/api/v2/occurrences', {
          body: { taskId: input.taskId, date: input.date, assigneeId: input.assigneeId } as {
            taskId: string;
            date: string;
            assigneeId: string | null;
            done: boolean | null;
            requestId: string | null;
          },
        }),
      );
      return toOccurrence(data);
    },
    onSuccess: refresh,
  });

  const doneNow = useMutation({
    mutationFn: async (input: { item: DueItemView; todayKey: string; choice?: DoneNowChoice }): Promise<Occurrence> => {
      // Today's planned occurrence is completed in place; otherwise the extra execution is recorded
      // as done in one request. The key makes a retry of the same click idempotent (ADR-0009).
      const planned = input.item.nextOccurrence;
      if (planned?.date === input.todayKey) {
        return (await sendOccurrenceAction({ id: planned.id, kind: 'complete', ...input.choice })).data;
      }
      // One key per intent, kept across retries and page changes until the request succeeded. The person is left out:
      // recorded work is credited to the actor.
      const intent = `done-now:${input.item.taskId}:${input.todayKey}`;
      const { data } = await unwrap(
        apiV2.POST('/api/v2/occurrences', {
          body: {
            taskId: input.item.taskId,
            date: input.todayKey,
            done: true,
            requestId: requestKeyFor(intent),
          } as { taskId: string; date: string; assigneeId: string | null; done: boolean | null; requestId: string },
        }),
      );
      releaseRequestKey(intent);
      return toOccurrence(data);
    },
    onSuccess: refresh,
  });

  return { plan, doneNow };
}

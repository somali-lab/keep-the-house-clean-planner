import type { OccurrenceView } from '@huishoudplanner/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, ApiRequestError, type ApiClient } from '../../api/index.ts';
import { releaseRequestKey, requestKeyFor } from '../../api/requestKey.ts';
import { useOfflineQueue } from '../../offline/context.ts';

export const occurrenceKeys = {
  range: (from: string, to: string) => ['occurrences', from, to] as const,
};

export function useOccurrences(from: string, to: string, enabled = true) {
  return useQuery({
    queryKey: occurrenceKeys.range(from, to),
    queryFn: async () => (await api.get<OccurrenceView[]>(`/api/occurrences?from=${from}&to=${to}`)).data,
    enabled,
  });
}

export type OccurrenceAction =
  | { id: string; kind: 'complete'; completedBy?: string; takeOver?: true }
  | { id: string; kind: 'uncomplete' }
  | { id: string; kind: 'skip'; reason?: string }
  | { id: string; kind: 'assign'; assigneeId: string | null }
  | { id: string; kind: 'claim' }
  /** Undo of recorded extra work: the occurrence is deleted rather than reopened (ADR-0009). */
  | { id: string; kind: 'retract' };

/** Actions that are PATCHed or claimed as one occurrence; retracting is a separate endpoint. */
export type SendableAction = Exclude<OccurrenceAction, { kind: 'retract' }>;

/**
 * Actions that can wait in the offline queue; claiming needs the server to decide who was first,
 * and a retract needs the server's answer to tell recorded work from planned work.
 */
export type QueueableAction = Exclude<OccurrenceAction, { kind: 'claim' } | { kind: 'assign' } | { kind: 'retract' }>;

export const isQueueable = (action: OccurrenceAction): action is QueueableAction =>
  action.kind !== 'claim' && action.kind !== 'assign' && action.kind !== 'retract';

/** What the server will do, applied locally so the list reacts instantly. */
export function applyOptimistic(
  occ: OccurrenceView,
  action: OccurrenceAction,
  context: { profileId: string; todayKey: string; now: Date },
): OccurrenceView {
  switch (action.kind) {
    case 'complete': {
      // The server credits the actor unless a named person is chosen; work of someone else always carries a choice.
      const completedBy = action.takeOver ? context.profileId : (action.completedBy ?? context.profileId);
      return {
        ...occ,
        status: 'done',
        statusBeforeCompletion: occ.status === 'skipped' ? 'skipped' : 'open',
        completedAt: context.now.toISOString(),
        completedBy,
        assigneeId: action.takeOver ? context.profileId : (occ.assigneeId ?? completedBy),
        isOverdue: false,
      };
    }
    case 'uncomplete': {
      const status = occ.statusBeforeCompletion ?? 'open';
      return {
        ...occ,
        status,
        statusBeforeCompletion: null,
        completedAt: null,
        completedBy: null,
        isOverdue: status === 'open' && occ.date < context.todayKey,
      };
    }
    case 'skip':
      return { ...occ, status: 'skipped', skipReason: action.reason ? action.reason : null, isOverdue: false };
    case 'assign':
      return { ...occ, assigneeId: action.assigneeId };
    case 'claim':
      return { ...occ, assigneeId: context.profileId };
    case 'retract':
      return occ; // the list drops the item instead; see useOccurrenceAction
  }
}

/** Sends one action; the offline sync passes a client that speaks for the profile that queued it. */
export function sendOccurrenceAction(action: SendableAction, client: ApiClient = api) {
  if (action.kind === 'claim') return client.post<OccurrenceView>(`/api/occurrences/${action.id}/claim`);
  if (action.kind === 'assign') {
    return client.patch<OccurrenceView>(`/api/occurrences/${action.id}`, {
      action: 'assign',
      assigneeId: action.assigneeId,
    });
  }
  const body =
    action.kind === 'complete'
      ? {
          action: 'complete',
          ...(action.completedBy ? { completedBy: action.completedBy } : {}),
          ...(action.takeOver ? { takeOver: true } : {}),
        }
      : action.kind === 'skip'
        ? { action: 'skip', ...(action.reason ? { reason: action.reason } : {}) }
        : { action: 'uncomplete' };
  return client.patch<OccurrenceView>(`/api/occurrences/${action.id}`, body);
}

/**
 * Optimistic occurrence actions with rollback on error. Inside OfflineSyncProvider,
 * a check-off that gets no answer at all is queued and keeps its optimistic state.
 */
export function useOccurrenceAction(
  queryKey: readonly unknown[],
  context: { profileId: string; todayKey: string },
) {
  const queryClient = useQueryClient();
  const offline = useOfflineQueue();
  const replace = (updater: (list: OccurrenceView[]) => OccurrenceView[]) =>
    queryClient.setQueryData<OccurrenceView[]>(queryKey, (list) => (list ? updater(list) : list));

  return useMutation({
    // Run even when the browser reports offline, so the action reaches the queue instead of pausing in memory.
    networkMode: 'always',
    mutationFn: async (action: OccurrenceAction): Promise<OccurrenceView | null> => {
      if (action.kind === 'retract') {
        try {
          await api.post(`/api/occurrences/${action.id}/retract`);
        } catch (error) {
          // A second retract finds nothing: the work is already undone.
          if (!(error instanceof ApiRequestError && error.status === 404)) throw error;
        }
        return null;
      }
      try {
        return (await sendOccurrenceAction(action)).data;
      } catch (error) {
        if (offline && isQueueable(action) && !(error instanceof ApiRequestError)) {
          const taskName =
            queryClient.getQueryData<OccurrenceView[]>(queryKey)?.find((occ) => occ._id === action.id)?.taskNameSnapshot ?? '';
          await offline.enqueue({ action, profileId: context.profileId, taskName });
          return null;
        }
        throw error;
      }
    },
    onMutate: async (action) => {
      await queryClient.cancelQueries({ queryKey });
      const previous = queryClient.getQueryData<OccurrenceView[]>(queryKey);
      replace((list) =>
        action.kind === 'retract'
          ? list.filter((occ) => occ._id !== action.id)
          : list.map((occ) => (occ._id === action.id ? applyOptimistic(occ, action, { ...context, now: new Date() }) : occ)),
      );
      return { previous };
    },
    onError: (_error, _action, result) => {
      if (result?.previous) queryClient.setQueryData(queryKey, result.previous);
    },
    onSuccess: (updated) => {
      if (updated) replace((list) => list.map((occ) => (occ._id === updated._id ? updated : occ)));
    },
    // A queued action keeps the optimistic list; a refetch would fail offline anyway. A retract
    // is never queued, so its list is refetched, and so is everything the deleted work fed: the due list
    // (it restarted the due clock), the tasks (lastCompletedAt) and the statistics.
    onSettled: (updated, error, action) => {
      if (action.kind === 'retract') {
        for (const key of ['due', 'tasks', 'stats']) void queryClient.invalidateQueries({ queryKey: [key] });
        return queryClient.invalidateQueries({ queryKey });
      }
      return updated === null && !error ? undefined : queryClient.invalidateQueries({ queryKey });
    },
  });
}

/**
 * Work that is not (or not in this form) in the plan (ADR-0009): either recorded as done today (`done`,
 * with the person who did it) or planned as an open occurrence on a day (`assigneeId` null: anyone).
 */
export type RecordWorkInput =
  | { kind: 'extra'; taskId: string; date: string; assigneeId: string | null; done: boolean }
  | {
      kind: 'oneOff';
      name: string;
      roomId: string | null;
      durationMinutes: number;
      date: string;
      assigneeId: string | null;
      done: boolean;
    };

/**
 * Records an extra execution or a one-off task as done, or plans it as an open occurrence, in one request. The request key belongs to the
 * intent (kind, task or name, date, person, ...): a repeated click, a retry, or a closed and reopened
 * dialog with the same values reuses it, so the server stores one record; it is dropped once the request
 * succeeded. Not queued offline: the server decides whether the record is new.
 */
export function useRecordWork() {
  const queryClient = useQueryClient();
  return useMutation({
    networkMode: 'always',
    mutationFn: async (input: RecordWorkInput): Promise<OccurrenceView> => {
      const intent = `record-work:${JSON.stringify(input)}`;
      const requestId = requestKeyFor(intent);
      const created =
        input.kind === 'extra'
          ? (
              await api.post<OccurrenceView>('/api/occurrences', {
                taskId: input.taskId,
                date: input.date,
                assigneeId: input.assigneeId,
                ...(input.done ? { done: true } : {}),
                requestId,
              })
            ).data
          : (
              await api.post<OccurrenceView>('/api/occurrences/one-off', {
                name: input.name,
                roomId: input.roomId,
                durationMinutes: input.durationMinutes,
                date: input.date,
                assigneeId: input.assigneeId,
                ...(input.done ? { done: true } : {}),
                requestId,
              })
            ).data;
      releaseRequestKey(intent);
      return created;
    },
    // Occurrences, the due list, tasks (lastCompletedAt) and every statistic read the new record.
    onSettled: () =>
      Promise.all(
        ['occurrences', 'due', 'tasks', 'stats'].map((key) => queryClient.invalidateQueries({ queryKey: [key] })),
      ),
  });
}

/**
 * Completes the planned occurrence of a task (for the person who did it) instead of recording an extra
 * execution. It is the same PATCH as a check-off; the key is not needed because completing twice is refused.
 */
export function useCheckOffPlanned() {
  const queryClient = useQueryClient();
  return useMutation({
    networkMode: 'always',
    mutationFn: async (input: { id: string; completedBy: string }): Promise<OccurrenceView> =>
      (await sendOccurrenceAction({ id: input.id, kind: 'complete', completedBy: input.completedBy })).data,
    onSettled: () =>
      Promise.all(
        ['occurrences', 'due', 'tasks', 'stats'].map((key) => queryClient.invalidateQueries({ queryKey: [key] })),
      ),
  });
}

import type { OccurrenceView } from '@huishoudplanner/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, ApiRequestError, type ApiClient } from '../../api/index.ts';
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
  | { id: string; kind: 'claim' };

/** Actions that can wait in the offline queue; claiming needs the server to decide who was first. */
export type QueueableAction = Exclude<OccurrenceAction, { kind: 'claim' }>;

export const isQueueable = (action: OccurrenceAction): action is QueueableAction => action.kind !== 'claim';

/** What the server will do, applied locally so the list reacts instantly. */
export function applyOptimistic(
  occ: OccurrenceView,
  action: OccurrenceAction,
  context: { profileId: string; todayKey: string; now: Date },
): OccurrenceView {
  switch (action.kind) {
    case 'complete': {
      const completedBy = action.takeOver
        ? context.profileId
        : (action.completedBy ?? occ.assigneeId ?? context.profileId);
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
    case 'claim':
      return { ...occ, assigneeId: context.profileId };
  }
}

/** Sends one action; the offline sync passes a client that speaks for the profile that queued it. */
export function sendOccurrenceAction(action: OccurrenceAction, client: ApiClient = api) {
  if (action.kind === 'claim') return client.post<OccurrenceView>(`/api/occurrences/${action.id}/claim`);
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
        list.map((occ) => (occ._id === action.id ? applyOptimistic(occ, action, { ...context, now: new Date() }) : occ)),
      );
      return { previous };
    },
    onError: (_error, _action, result) => {
      if (result?.previous) queryClient.setQueryData(queryKey, result.previous);
    },
    onSuccess: (updated) => {
      if (updated) replace((list) => list.map((occ) => (occ._id === updated._id ? updated : occ)));
    },
    // A queued action keeps the optimistic list; a refetch would fail offline anyway.
    onSettled: (updated, error) => (updated === null && !error ? undefined : queryClient.invalidateQueries({ queryKey })),
  });
}

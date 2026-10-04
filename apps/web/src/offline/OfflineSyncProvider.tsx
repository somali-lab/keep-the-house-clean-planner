import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { ApiRequestError, createV2Client, unwrap, type ApiV2Client } from '../api/index.ts';
import { sendOccurrenceAction, type QueueableAction } from '../features/today/api.ts';
import { format, t } from '../i18n/nl.ts';
import { OfflineQueueContext, type OfflineQueue } from './context.ts';
import { defaultStore, type QueueStore } from './queue.ts';

/** Extra retry while changes wait, for when the server was down rather than the device offline. */
export const RETRY_INTERVAL_MS = 30_000;

/** Answers that say "try again later" rather than "this can never work": the change stays in the queue. */
const RETRYABLE_STATUSES = new Set([408, 425, 429]);

/** Whether a failed replay must stay queued: no answer at all, a 5xx, or a transient 4xx. */
export function isRetryable(error: unknown): boolean {
  if (!(error instanceof ApiRequestError)) return true;
  return error.status >= 500 || RETRYABLE_STATUSES.has(error.status);
}

/**
 * Whether the occurrence is already in the state the queued action wanted. A replay then answers
 * `409 invalid_transition` although nothing is wrong: the first request reached the server and only
 * its answer was lost.
 */
export function alreadyApplied(
  action: QueueableAction,
  occurrence: { status: string; completedBy: string | null },
  profileId: string,
): boolean {
  const status = occurrence.status;
  switch (action.kind) {
    case 'complete':
      // Done by someone else meanwhile is a real conflict; only the credited person this replay would have named counts.
      return status === 'done' && occurrence.completedBy === (action.completedBy ?? profileId);
    case 'skip':
      return status === 'skipped';
    case 'uncomplete':
      return status !== 'done';
  }
}

/** A client whose reads skip every cache, the service worker's included, so the check sees the stored state. */
function freshClient(profileId: string): ApiV2Client {
  return createV2Client({ getProfileId: () => profileId, fetchImpl: (input, init) => fetch(input, { ...init, cache: 'no-store' }) });
}

async function wasAlreadyApplied(action: QueueableAction, profileId: string, error: unknown): Promise<boolean> {
  if (!(error instanceof ApiRequestError) || error.status !== 409 || error.code !== 'invalid_transition') return false;
  try {
    const { data } = await unwrap(freshClient(profileId).GET('/api/v2/occurrences/{id}', { params: { path: { id: action.id } } }));
    return alreadyApplied(action, data, profileId);
  } catch {
    return false;
  }
}

/**
 * Queues check-offs that got no answer and sends them in order once online. The queued actions are the
 * intent endpoints of Today (complete, uncomplete, skip): they carry no `If-Match` (ADR-0022), so a replay
 * never meets `412` or `428`. A permanent 4xx answer (the occurrence changed or is gone, or the request is
 * refused) drops the change and reports it, so a replay never loops; a replay that finds the occurrence
 * already in the wanted state is done silently. No answer, a 5xx or a transient 4xx (408, 425, 429) keeps
 * the queue for the next attempt.
 */
export function OfflineSyncProvider({ children, store: givenStore }: { children: ReactNode; store?: QueueStore }) {
  const queryClient = useQueryClient();
  const [store] = useState(() => givenStore ?? defaultStore());
  const [pending, setPending] = useState(0);
  const [conflicts, setConflicts] = useState<string[]>([]);
  const running = useRef(false);

  const refresh = useCallback(async () => setPending((await store.all()).length), [store]);

  const sync = useCallback(async () => {
    if (running.current) return;
    running.current = true;
    let changed = false;
    try {
      for (const item of await store.all()) {
        const client = createV2Client({ getProfileId: () => item.profileId });
        try {
          await sendOccurrenceAction(item.action, client);
        } catch (error) {
          if (isRetryable(error)) break;
          if (!(await wasAlreadyApplied(item.action, item.profileId, error))) {
            setConflicts((current) => [...current, item.taskName]);
          }
        }
        await store.remove(item.seq);
        changed = true;
      }
    } finally {
      running.current = false;
      await refresh();
      if (changed) await queryClient.invalidateQueries({ queryKey: ['occurrences'] });
    }
  }, [store, refresh, queryClient]);

  useEffect(() => {
    void sync();
    const onOnline = () => void sync();
    window.addEventListener('online', onOnline);
    return () => window.removeEventListener('online', onOnline);
  }, [sync]);

  useEffect(() => {
    if (pending === 0) return;
    const timer = setInterval(() => void sync(), RETRY_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [pending, sync]);

  const queue = useMemo<OfflineQueue>(
    () => ({
      enqueue: async (item) => {
        await store.add({ ...item, queuedAt: new Date().toISOString() });
        await refresh();
      },
    }),
    [store, refresh],
  );

  return (
    <OfflineQueueContext.Provider value={queue}>
      {pending > 0 && (
        <p className="fixed inset-x-4 bottom-24 z-[70] mx-auto max-w-xl rounded-2xl border border-warning bg-card p-4 font-semibold shadow-xl" role="status">
          {pending === 1 ? t('offline.pendingOne') : format('offline.pendingMany', { count: pending })}
        </p>
      )}
      {conflicts.length > 0 && (
        <div className="fixed inset-x-4 bottom-24 z-[70] mx-auto max-w-xl rounded-2xl border border-destructive/40 bg-card p-4 text-destructive shadow-xl" role="alert">
          {conflicts.map((task, index) => (
            <p key={index}>{format('offline.conflict', { task })}</p>
          ))}
          <Button type="button" variant="outline" className="mt-3" onClick={() => setConflicts([])}>
            {t('common.close')}
          </Button>
        </div>
      )}
      {children}
    </OfflineQueueContext.Provider>
  );
}

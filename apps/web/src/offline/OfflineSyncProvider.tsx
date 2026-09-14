import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { ApiRequestError, createApiClient } from '../api/client.ts';
import { sendOccurrenceAction } from '../features/today/api.ts';
import { format, t } from '../i18n/nl.ts';
import { OfflineQueueContext, type OfflineQueue } from './context.ts';
import { defaultStore, type QueueStore } from './queue.ts';

/** Extra retry while changes wait, for when the server was down rather than the device offline. */
export const RETRY_INTERVAL_MS = 30_000;

/**
 * Queues check-offs that got no answer and sends them in order once online.
 * A 4xx answer means the occurrence changed meanwhile: the change is dropped
 * and reported. No answer or a 5xx keeps the queue for the next attempt.
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
        const client = createApiClient({ getProfileId: () => item.profileId });
        try {
          await sendOccurrenceAction(item.action, client);
        } catch (error) {
          if (!(error instanceof ApiRequestError) || error.status >= 500) break;
          setConflicts((current) => [...current, item.taskName]);
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
        <p className="offline-banner" role="status">
          {pending === 1 ? t('offline.pendingOne') : format('offline.pendingMany', { count: pending })}
        </p>
      )}
      {conflicts.length > 0 && (
        <div className="offline-banner is-conflict" role="alert">
          {conflicts.map((task, index) => (
            <p key={index}>{format('offline.conflict', { task })}</p>
          ))}
          <button type="button" onClick={() => setConflicts([])}>
            {t('common.close')}
          </button>
        </div>
      )}
      {children}
    </OfflineQueueContext.Provider>
  );
}

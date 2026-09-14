import { createContext, useContext } from 'react';
import type { QueuedActionInput } from './queue.ts';

export interface OfflineQueue {
  enqueue(item: QueuedActionInput): Promise<void>;
}

export const OfflineQueueContext = createContext<OfflineQueue | null>(null);

/** Null outside OfflineSyncProvider: failed actions then roll back as before. */
export function useOfflineQueue(): OfflineQueue | null {
  return useContext(OfflineQueueContext);
}

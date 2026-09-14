import type { QueueableAction } from '../features/today/api.ts';

/** A check-off made without a connection, waiting to be sent. */
export interface QueuedAction {
  seq: number;
  action: QueueableAction;
  /** Profile that made the change; the sync sends it as this profile, whoever is active by then. */
  profileId: string;
  /** For the conflict message. */
  taskName: string;
  queuedAt: string;
}

export type QueuedActionInput = Omit<QueuedAction, 'seq' | 'queuedAt'>;

export interface QueueStore {
  add(item: Omit<QueuedAction, 'seq'>): Promise<void>;
  /** In the order they were added. */
  all(): Promise<QueuedAction[]>;
  remove(seq: number): Promise<void>;
}

export function memoryStore(): QueueStore {
  let next = 1;
  let items: QueuedAction[] = [];
  return {
    add: async (item) => {
      items = [...items, { ...item, seq: next++ }];
    },
    all: async () => [...items],
    remove: async (seq) => {
      items = items.filter((item) => item.seq !== seq);
    },
  };
}

const STORE = 'actions';

function done<T>(request: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

/** Survives reloads and closed tabs. Keys auto-increment, so getAll() keeps the order. */
export function indexedDbStore(dbName = 'huishoudplanner-offline'): QueueStore {
  let opened: Promise<IDBDatabase> | null = null;
  const database = () =>
    (opened ??= new Promise((resolve, reject) => {
      const request = indexedDB.open(dbName, 1);
      request.onupgradeneeded = () => request.result.createObjectStore(STORE, { keyPath: 'seq', autoIncrement: true });
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    }));
  const run = async <T>(mode: IDBTransactionMode, fn: (store: IDBObjectStore) => IDBRequest<T>) =>
    done(fn((await database()).transaction(STORE, mode).objectStore(STORE)));

  return {
    add: async (item) => {
      await run('readwrite', (store) => store.add(item));
    },
    all: () => run('readonly', (store) => store.getAll()) as Promise<QueuedAction[]>,
    remove: async (seq) => {
      await run('readwrite', (store) => store.delete(seq));
    },
  };
}

/** IndexedDB in the browser; in memory where it does not exist (jsdom). */
export function defaultStore(): QueueStore {
  return typeof indexedDB === 'undefined' ? memoryStore() : indexedDbStore();
}

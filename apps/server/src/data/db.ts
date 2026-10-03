import { MongoClient, type Db, type IndexDescription } from 'mongodb';

export const COLLECTIONS = {
  users: 'users',
  rooms: 'rooms',
  tasks: 'tasks',
  cyclePlans: 'cyclePlans',
  cycles: 'cycles',
  occurrences: 'occurrences',
  auditLog: 'auditLog',
  settings: 'settings',
  pointEntries: 'pointEntries',
} as const;

export type CollectionName = (typeof COLLECTIONS)[keyof typeof COLLECTIONS];

/** MongoDB error code for dropping an index that no longer exists. */
const INDEX_NOT_FOUND = 27;

export const GENERATED_SLOT_INDEX = 'occurrences_generated_slot_unique';

/** Every index the app relies on (requirements §2, §3.8 and plan §1.4). */
export const INDEXES: Record<CollectionName, IndexDescription[]> = {
  users: [],
  rooms: [],
  settings: [],
  tasks: [{ key: { roomId: 1, active: 1 } }],
  cyclePlans: [{ key: { 'slots.weekIndex': 1, 'slots.weekday': 1 } }],
  cycles: [{ key: { index: 1 }, unique: true }],
  occurrences: [
    { key: { date: 1, assigneeId: 1 } },
    { key: { status: 1, date: 1 } },
    { key: { taskId: 1, completedAt: -1 } },
    {
      key: { cycleId: 1, taskId: 1, plannedDate: 1 },
      name: GENERATED_SLOT_INDEX,
      unique: true,
      partialFilterExpression: { origin: 'generated' },
    },
    {
      key: { requestId: 1 },
      name: 'occurrences_request_id_unique',
      unique: true,
      partialFilterExpression: { requestId: { $type: 'string' } },
    },
  ],
  auditLog: [{ key: { entity: 1, entityId: 1, at: -1 } }, { key: { at: -1 } }],
  // ADR-0011: the key makes the ledger idempotent, one entry per execution.
  pointEntries: [
    { key: { key: 1 }, name: 'pointEntries_key_unique', unique: true },
    { key: { personId: 1, date: -1 } },
    { key: { date: 1 } },
  ],
};

export async function connectMongo(url: string): Promise<{ client: MongoClient; db: Db }> {
  const client = new MongoClient(url, { serverSelectionTimeoutMS: 10_000 });
  await client.connect();
  return { client, db: client.db() };
}

/**
 * ADR-0009: the slot key (cycleId, taskId, plannedDate) was unique for every
 * occurrence; it is now unique for generated occurrences only. Drops any other
 * index with exactly that key (including the legacy default name) so the
 * partial index can be created. Idempotent; a schema change, not audited.
 */
async function dropLegacySlotIndexes(db: Db): Promise<void> {
  const exists = await db.listCollections({ name: COLLECTIONS.occurrences }, { nameOnly: true }).toArray();
  if (exists.length === 0) return;
  const collection = db.collection(COLLECTIONS.occurrences);
  const legacyKey = JSON.stringify({ cycleId: 1, taskId: 1, plannedDate: 1 });
  for (const index of await collection.indexes()) {
    if (JSON.stringify(index.key) === legacyKey && index.name !== GENERATED_SLOT_INDEX) {
      try {
        await collection.dropIndex(index.name!);
      } catch (error) {
        // A concurrent startup dropped it first (IndexNotFound): the goal is reached.
        if ((error as { code?: number }).code !== INDEX_NOT_FOUND) throw error;
      }
    }
  }
}

export async function ensureIndexes(db: Db): Promise<void> {
  await dropLegacySlotIndexes(db);
  for (const [name, indexes] of Object.entries(INDEXES)) {
    const existing = await db.listCollections({ name }, { nameOnly: true }).toArray();
    if (existing.length === 0) await db.createCollection(name);
    if (indexes.length > 0) await db.collection(name).createIndexes(indexes);
  }
}

export async function pingMongo(db: Db): Promise<boolean> {
  try {
    await db.command({ ping: 1 });
    return true;
  } catch {
    return false;
  }
}

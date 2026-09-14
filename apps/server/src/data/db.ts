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
} as const;

export type CollectionName = (typeof COLLECTIONS)[keyof typeof COLLECTIONS];

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
    { key: { cycleId: 1, taskId: 1, plannedDate: 1 }, unique: true },
  ],
  auditLog: [{ key: { entity: 1, entityId: 1, at: -1 } }, { key: { at: -1 } }],
};

export async function connectMongo(url: string): Promise<{ client: MongoClient; db: Db }> {
  const client = new MongoClient(url, { serverSelectionTimeoutMS: 10_000 });
  await client.connect();
  return { client, db: client.db() };
}

export async function ensureIndexes(db: Db): Promise<void> {
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

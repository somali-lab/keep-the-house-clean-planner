import { MongoBulkWriteError, type Db, type Document } from 'mongodb';
import { COLLECTIONS } from './db.ts';

/** Every collection in an export, in the order they are replaced on import. */
export const TRANSFER_COLLECTIONS = [
  COLLECTIONS.settings,
  COLLECTIONS.users,
  COLLECTIONS.rooms,
  COLLECTIONS.tasks,
  COLLECTIONS.cyclePlans,
  COLLECTIONS.cycles,
  COLLECTIONS.occurrences,
  COLLECTIONS.auditLog,
] as const;
export type TransferCollection = (typeof TRANSFER_COLLECTIONS)[number];
export type ReplacedCollection = Exclude<TransferCollection, 'auditLog'>;
export type TransferDocs = Record<TransferCollection, Document[]>;

/** Raw documents of every collection, ordered by _id. */
export async function readAllCollections(db: Db): Promise<TransferDocs> {
  const entries = await Promise.all(
    TRANSFER_COLLECTIONS.map(async (name) => [name, await db.collection(name).find({}).sort({ _id: 1 }).toArray()] as const),
  );
  return Object.fromEntries(entries) as unknown as TransferDocs;
}

export interface ReplaceResult {
  replaced: Record<ReplacedCollection, number>;
  /** Imported audit entries that were not in the log yet. */
  auditAdded: number;
}

/**
 * Replaces every collection with the given documents, except the audit log:
 * existing entries stay (append-only) and imported entries are added unless
 * their _id is already present. Validate everything before calling this; the
 * caller records one audit entry for the whole import.
 */
export async function replaceAllCollections(db: Db, docs: TransferDocs): Promise<ReplaceResult> {
  const replaced = {} as Record<ReplacedCollection, number>;
  for (const name of TRANSFER_COLLECTIONS) {
    if (name === COLLECTIONS.auditLog) continue;
    const collection = db.collection(name);
    await collection.deleteMany({});
    if (docs[name].length > 0) await collection.insertMany(docs[name], { ordered: true });
    replaced[name] = docs[name].length;
  }

  const audit = docs[COLLECTIONS.auditLog];
  let duplicates = 0;
  if (audit.length > 0) {
    try {
      await db.collection(COLLECTIONS.auditLog).insertMany(audit, { ordered: false });
    } catch (err) {
      if (!(err instanceof MongoBulkWriteError)) throw err;
      const writeErrors = Array.isArray(err.writeErrors) ? err.writeErrors : [err.writeErrors];
      if (writeErrors.some((e) => e.code !== 11000)) throw err;
      duplicates = writeErrors.length;
    }
  }
  return { replaced, auditAdded: audit.length - duplicates };
}

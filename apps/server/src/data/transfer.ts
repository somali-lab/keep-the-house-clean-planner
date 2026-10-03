import { MongoBulkWriteError, type Db, type Document } from 'mongodb';
import { COLLECTIONS } from './db.ts';
import { clearBadgeAwards, countBadges } from './badges.ts';
import { clearPointEntries, countRedemptions } from './points.ts';

/**
 * Every collection in an export, in the order they are replaced on import. `pointEntries` holds only
 * the redemptions: they are booked, so they cannot be derived; every other ledger entry is rebuilt (ADR-0013).
 * `badges` holds the badge definitions with their images; the awards are derived and rebuilt (ADR-0014).
 */
export const TRANSFER_COLLECTIONS = [
  COLLECTIONS.settings,
  COLLECTIONS.users,
  COLLECTIONS.rooms,
  COLLECTIONS.tasks,
  COLLECTIONS.cyclePlans,
  COLLECTIONS.cycles,
  COLLECTIONS.occurrences,
  COLLECTIONS.pointEntries,
  COLLECTIONS.badges,
  COLLECTIONS.auditLog,
] as const;
export type TransferCollection = (typeof TRANSFER_COLLECTIONS)[number];
export type ReplacedCollection = Exclude<TransferCollection, 'auditLog'>;
export type TransferDocs = Record<TransferCollection, Document[]>;

/** Raw documents of every collection, ordered by _id. */
export async function readAllCollections(db: Db): Promise<TransferDocs> {
  const entries = await Promise.all(
    TRANSFER_COLLECTIONS.map(
      async (name) =>
        [name, await db.collection(name).find(name === COLLECTIONS.pointEntries ? { kind: 'redemption' } : {}).sort({ _id: 1 }).toArray()] as const,
    ),
  );
  return Object.fromEntries(entries) as unknown as TransferDocs;
}

export interface ReplaceResult {
  replaced: Record<ReplacedCollection, number>;
  /** Imported audit entries that were not in the log yet. */
  auditAdded: number;
  /** Entries of the points ledger that were dropped; the caller rebuilds the derived entries. */
  removedPointEntries: number;
  /** The redemptions among them: a file of version 4 or older has none to put back (ADR-0013). */
  removedRedemptions: number;
  /** The badges that existed before the import replaced them; a file older than version 6 has none to put back (ADR-0014). */
  removedBadges: number;
  /** Badge awards that were dropped; the caller rebuilds them from the imported data (ADR-0014). */
  removedBadgeAwards: number;
}

/**
 * Replaces every collection with the given documents, except the audit log:
 * existing entries stay (append-only) and imported entries are added unless
 * their _id is already present. Validate everything before calling this; the
 * caller records one audit entry for the whole import.
 */
export async function replaceAllCollections(db: Db, docs: TransferDocs): Promise<ReplaceResult> {
  const replaced = {} as Record<ReplacedCollection, number>;
  const removedBadges = await countBadges(db);
  for (const name of TRANSFER_COLLECTIONS) {
    // The ledger is handled below: only the redemptions come from the file.
    if (name === COLLECTIONS.auditLog || name === COLLECTIONS.pointEntries) continue;
    const collection = db.collection(name);
    await collection.deleteMany({});
    if (docs[name].length > 0) await collection.insertMany(docs[name], { ordered: true });
    replaced[name] = docs[name].length;
  }

  // The derived part of the ledger is not exported: the old ledger is dropped, the booked redemptions of the file
  // are put back, and the caller rebuilds the rest from the new occurrences (ADR-0011, ADR-0013).
  const removedRedemptions = await countRedemptions(db);
  const removedPointEntries = await clearPointEntries(db);
  const redemptions = docs[COLLECTIONS.pointEntries];
  if (redemptions.length > 0) await db.collection(COLLECTIONS.pointEntries).insertMany(redemptions, { ordered: true });
  replaced.pointEntries = redemptions.length;

  // Awards are derived from the executions and the badge definitions of the file: drop them, the caller rebuilds them (ADR-0014).
  const removedBadgeAwards = await clearBadgeAwards(db);

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
  return { replaced, auditAdded: audit.length - duplicates, removedPointEntries, removedRedemptions, removedBadges, removedBadgeAwards };
}

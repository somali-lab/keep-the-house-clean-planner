import type { Db } from 'mongodb';
import { auditLogCollection } from './auditLog.ts';

/**
 * The single exception to the append-only audit log: removes entries older
 * than the cutoff. Only the audit retention job (AUDIT_RETENTION_DAYS) calls this.
 */
export async function deleteAuditEntriesBefore(db: Db, cutoff: Date): Promise<number> {
  const result = await auditLogCollection(db).deleteMany({ at: { $lt: cutoff } });
  return result.deletedCount;
}

import type { FastifyBaseLogger } from 'fastify';
import type { Db } from 'mongodb';
import type { Clock } from '../clock.ts';
import { deleteAuditEntriesBefore } from '../data/auditRetention.ts';

const DAY_MS = 24 * 60 * 60 * 1000;

export type AuditRetentionResult = { status: 'disabled' } | { status: 'done'; cutoff: string; deleted: number };

/** Deletes audit entries older than AUDIT_RETENTION_DAYS; does nothing when that is not set. */
export async function runAuditRetention(deps: {
  db: Db;
  clock: Clock;
  log: FastifyBaseLogger;
  retentionDays: number | undefined;
}): Promise<AuditRetentionResult> {
  if (deps.retentionDays === undefined) return { status: 'disabled' };
  const cutoff = new Date(deps.clock.now().getTime() - deps.retentionDays * DAY_MS);
  const deleted = await deleteAuditEntriesBefore(deps.db, cutoff);
  deps.log.info({ cutoff, deleted }, 'audit retention completed');
  return { status: 'done', cutoff: cutoff.toISOString(), deleted };
}

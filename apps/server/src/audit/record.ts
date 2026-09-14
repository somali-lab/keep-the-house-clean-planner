import type { AuditAction, AuditEntity } from '@huishoudplanner/shared';
import type { ObjectId } from 'mongodb';
import { insertAuditEntry, type AuditEntryDoc } from '../data/auditLog.ts';
import type { AuditContext } from './context.ts';

export interface AuditInput {
  entity: AuditEntity;
  entityId: ObjectId;
  action: AuditAction;
  before?: Record<string, unknown>;
  after?: Record<string, unknown>;
  meta?: Record<string, unknown>;
}

/**
 * The only way to add an audit entry. Called by repositories right after a
 * successful write. There are no transactions, so a failing audit insert is
 * logged with full context instead of undoing the write.
 */
export async function record(ctx: AuditContext, input: AuditInput): Promise<void> {
  const entry: Omit<AuditEntryDoc, '_id'> = {
    at: ctx.clock.now(),
    actorId: ctx.actorId,
    entity: input.entity,
    entityId: input.entityId,
    action: input.action,
    before: input.before ?? {},
    after: input.after ?? {},
    source: ctx.source,
    ...(input.meta ? { meta: input.meta } : {}),
  };
  try {
    await insertAuditEntry(ctx.db, entry);
  } catch (err) {
    ctx.log.error({ err, audit: entry }, 'audit_record_failed');
  }
}

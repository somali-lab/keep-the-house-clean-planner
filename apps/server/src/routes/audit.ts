import { listAuditQuerySchema } from '@huishoudplanner/shared';
import type { FastifyPluginAsync } from 'fastify';
import { ObjectId } from 'mongodb';
import { clearAuditEntries, findAuditEntries, type AuditEntryDoc } from '../data/auditLog.ts';
import { findOccurrences } from '../data/occurrences.ts';
import { HttpError, parseOrThrow } from '../http/errors.ts';
import { toApi } from '../http/serialize.ts';
import { requireAdmin } from '../identity/index.ts';

const OBJECT_ID_RE = /^[0-9a-f]{24}$/;

/** Opaque keyset cursor: position of the last returned entry. */
export function encodeCursor(entry: Pick<AuditEntryDoc, 'at' | '_id'>): string {
  return Buffer.from(`${entry.at.toISOString()}|${entry._id.toHexString()}`, 'utf8').toString('base64url');
}

export function decodeCursor(cursor: string): { at: Date; id: ObjectId } | null {
  const [iso, hex] = Buffer.from(cursor, 'base64url').toString('utf8').split('|');
  if (!iso || !hex || !OBJECT_ID_RE.test(hex)) return null;
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return null;
  return { at, id: new ObjectId(hex) };
}

export const auditRoutes: FastifyPluginAsync = async (app) => {
  app.get('/audit', async (request) => {
    const query = parseOrThrow(listAuditQuerySchema, request.query);
    const after = query.cursor ? decodeCursor(query.cursor) : undefined;
    if (after === null) {
      throw new HttpError(400, 'validation_error', 'Invalid cursor', [{ field: 'cursor', message: 'invalid_cursor' }]);
    }

    const docs = await findAuditEntries(app.deps.db, {
      ...(query.entity ? { entity: query.entity } : {}),
      ...(query.entityId ? { entityId: new ObjectId(query.entityId) } : {}),
      ...(query.actorId ? { actorId: new ObjectId(query.actorId) } : {}),
      ...(query.source ? { source: query.source } : {}),
      ...(query.from ? { from: new Date(query.from) } : {}),
      ...(query.to ? { to: new Date(query.to) } : {}),
      ...(after ? { after } : {}),
      limit: query.limit + 1,
    });

    const page = docs.slice(0, query.limit);
    const last = page.at(-1);
    const occurrenceIds = page
      .filter((entry) => entry.entity === 'occurrence' && !entry.meta?.occurrence)
      .map((entry) => entry.entityId);
    const occurrences = occurrenceIds.length > 0
      ? await findOccurrences(app.deps.db, { _id: { $in: occurrenceIds } })
      : [];
    const occurrenceById = new Map(occurrences.map((occurrence) => [occurrence._id.toHexString(), occurrence]));
    const enrichedPage = page.map((entry) => {
      if (entry.entity !== 'occurrence' || entry.meta?.occurrence) return entry;
      const occurrence = occurrenceById.get(entry.entityId.toHexString());
      if (!occurrence) return entry;
      return {
        ...entry,
        meta: {
          ...entry.meta,
          occurrence: {
            taskNameSnapshot: occurrence.taskNameSnapshot,
            roomNameSnapshot: occurrence.roomNameSnapshot ?? null,
            date: occurrence.date,
          },
        },
      };
    });
    return {
      items: toApi(enrichedPage),
      nextCursor: docs.length > query.limit && last ? encodeCursor(last) : null,
    };
  });

  // Deliberately not audited: recording this action would immediately make a
  // user-requested empty history non-empty again.
  app.delete('/audit', { preHandler: requireAdmin }, async () => ({
    deleted: await clearAuditEntries(app.deps.db),
  }));
};

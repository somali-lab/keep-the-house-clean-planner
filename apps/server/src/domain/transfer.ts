import {
  auditEntrySchema,
  cyclePlanSchema,
  cycleSchema,
  isoDateTimeSchema,
  objectIdSchema,
  occurrenceSchema,
  roomSchema,
  settingsSchema,
  taskSchema,
  userSchema,
} from '@huishoudplanner/shared';
import { BSON, ObjectId, type Db, type Document } from 'mongodb';
import { z } from 'zod';
import type { AuditContext } from '../audit/context.ts';
import { record } from '../audit/record.ts';
import { SETTINGS_ID } from '../data/settings.ts';
import { reconcilePoints, reconcilePointsSafely } from './points.ts';
import {
  readAllCollections,
  replaceAllCollections,
  TRANSFER_COLLECTIONS,
  type ReplaceResult,
  type TransferCollection,
  type TransferDocs,
} from '../data/transfer.ts';
import { HttpError, parseOrThrow, zodIssues, type FieldIssue } from '../http/errors.ts';
import { toApi } from '../http/serialize.ts';

/**
 * Written by every export. Version 2 adds `recordedDone`, `requestId` and a nullable occurrence
 * `taskId` (ADR-0009). Version 3 adds `tasks.points` and `occurrences.pointsSnapshot`; the points
 * ledger itself is not exported but rebuilt on import (ADR-0011). An import also accepts versions 1
 * and 2, which are valid unchanged because the new fields are optional; the rebuild fills them in.
 */
export const EXPORT_SCHEMA_VERSION = 3;

/**
 * Export file. `collections` is MongoDB relaxed Extended JSON (`{"$oid"}`,
 * `{"$date"}`), so ObjectIds and Dates survive the round trip unchanged.
 */
export interface ExportFile {
  schemaVersion: typeof EXPORT_SCHEMA_VERSION;
  exportedAt: string;
  collections: Record<TransferCollection, Record<string, unknown>[]>;
}

const rawDocs = z.array(z.record(z.string(), z.unknown()));

const envelopeSchema = z.object({
  schemaVersion: z.union([z.literal(1), z.literal(2), z.literal(3)]),
  exportedAt: isoDateTimeSchema,
  collections: z.object({
    settings: rawDocs,
    users: rawDocs,
    rooms: rawDocs,
    tasks: rawDocs,
    cyclePlans: rawDocs,
    cycles: rawDocs,
    occurrences: rawDocs,
    auditLog: rawDocs,
  }),
});

/** Shape checks on the API form of each stored document (ObjectId → hex, Date → ISO). */
const DOC_SCHEMAS: Record<TransferCollection, z.ZodType<Record<string, unknown>>> = {
  settings: settingsSchema.extend({ _id: objectIdSchema }),
  users: userSchema,
  rooms: roomSchema,
  tasks: taskSchema,
  cyclePlans: cyclePlanSchema,
  cycles: cycleSchema,
  // Stored as Dates at local midnight; the API shows them as day keys.
  occurrences: occurrenceSchema.extend({ date: isoDateTimeSchema, plannedDate: isoDateTimeSchema }),
  auditLog: auditEntrySchema,
};

/** Fields that must be real BSON types in the file (the API form hides the difference with strings). */
const TIMESTAMPS = ['createdAt', 'updatedAt'];
const TYPED_FIELDS: Record<TransferCollection, { ids: string[]; dates: string[] }> = {
  settings: { ids: [], dates: TIMESTAMPS },
  users: { ids: [], dates: TIMESTAMPS },
  rooms: { ids: [], dates: TIMESTAMPS },
  tasks: { ids: ['roomId', 'defaultAssigneeId'], dates: ['lastCompletedAt', ...TIMESTAMPS] },
  cyclePlans: { ids: [], dates: TIMESTAMPS },
  cycles: { ids: ['planId'], dates: ['generatedAt'] },
  occurrences: {
    ids: ['taskId', 'cycleId', 'planId', 'assigneeId', 'completedBy'],
    dates: ['date', 'plannedDate', 'completedAt', ...TIMESTAMPS],
  },
  auditLog: { ids: ['actorId', 'entityId'], dates: ['at'] },
};

function typeIssues(name: TransferCollection, doc: Document, path: string): FieldIssue[] {
  const issues: FieldIssue[] = [];
  const expectId = (value: unknown, field: string, nullable: boolean) => {
    if (!(value instanceof ObjectId) && !(nullable && value === null)) issues.push({ field, message: 'expected_object_id' });
  };
  expectId(doc._id, `${path}._id`, false);
  for (const field of TYPED_FIELDS[name].ids) expectId(doc[field], `${path}.${field}`, true);
  for (const field of TYPED_FIELDS[name].dates) {
    const value = doc[field];
    if (!(value instanceof Date) && value !== null) issues.push({ field: `${path}.${field}`, message: 'expected_date' });
  }
  if (name === 'cyclePlans' && Array.isArray(doc.slots)) {
    doc.slots.forEach((slot: Document, i: number) => {
      expectId(slot.taskId, `${path}.slots.${i}.taskId`, false);
      expectId(slot.assigneeId, `${path}.slots.${i}.assigneeId`, true);
    });
  }
  return issues;
}

/**
 * Occurrences that would violate a unique index (generated slot key, requestId). Replacing the
 * data deletes first and inserts after, so such a file has to be refused before anything is written.
 */
function duplicateKeyIssues(occurrences: Document[]): FieldIssue[] {
  const issues: FieldIssue[] = [];
  const slots = new Set<string>();
  const requestIds = new Set<string>();
  occurrences.forEach((doc, i) => {
    const path = `collections.occurrences.${i}`;
    if (doc.origin === 'generated' && doc.cycleId instanceof ObjectId && doc.plannedDate instanceof Date) {
      const key = `${doc.cycleId.toHexString()}:${doc.taskId instanceof ObjectId ? doc.taskId.toHexString() : 'null'}:${doc.plannedDate.getTime()}`;
      if (slots.has(key)) issues.push({ field: `${path}.plannedDate`, message: 'duplicate_slot' });
      slots.add(key);
    }
    if (typeof doc.requestId === 'string') {
      if (requestIds.has(doc.requestId)) issues.push({ field: `${path}.requestId`, message: 'duplicate_request_id' });
      requestIds.add(doc.requestId);
    }
  });
  return issues;
}

export async function buildExport(db: Db, now: Date): Promise<ExportFile> {
  const docs = await readAllCollections(db);
  return {
    schemaVersion: EXPORT_SCHEMA_VERSION,
    exportedAt: now.toISOString(),
    collections: BSON.EJSON.serialize(docs, { relaxed: true }) as ExportFile['collections'],
  };
}

export interface ParsedImport {
  schemaVersion: number;
  exportedAt: string;
  docs: TransferDocs;
}

/** Validates the whole file before anything is written; throws 400 validation_error listing every issue. */
export function parseImport(body: unknown): ParsedImport {
  const envelope = parseOrThrow(envelopeSchema, body);
  let deserialized: Record<TransferCollection, Document[]>;
  try {
    deserialized = BSON.EJSON.deserialize(envelope.collections, { relaxed: true }) as Record<TransferCollection, Document[]>;
  } catch {
    throw new HttpError(400, 'validation_error', 'Invalid extended JSON', [{ field: 'collections', message: 'invalid_extended_json' }]);
  }

  const issues: FieldIssue[] = [];
  const docs = {} as TransferDocs;
  for (const name of TRANSFER_COLLECTIONS) {
    docs[name] = deserialized[name].map((doc, i) => {
      const path = `collections.${name}.${i}`;
      const parsed = DOC_SCHEMAS[name].safeParse(toApi(doc));
      if (!parsed.success) {
        issues.push(...zodIssues(parsed.error).map((issue) => ({ ...issue, field: `${path}.${issue.field}` })));
        return doc;
      }
      issues.push(...typeIssues(name, doc, path));
      // Keep only known fields, with their original BSON types.
      return Object.fromEntries(Object.keys(parsed.data).filter((key) => key in doc).map((key) => [key, doc[key]]));
    });
  }

  const settings = docs.settings;
  if (settings.length !== 1 || !(settings[0]!._id instanceof ObjectId) || !settings[0]!._id.equals(SETTINGS_ID)) {
    issues.push({ field: 'collections.settings', message: 'settings_singleton' });
  }
  if (!docs.users.some((u) => u.active === true)) {
    issues.push({ field: 'collections.users', message: 'no_active_user' });
  }
  // The keys are built from typed values, so check them only once every document has the right types.
  if (issues.length === 0) issues.push(...duplicateKeyIssues(docs.occurrences));
  if (issues.length > 0) throw new HttpError(400, 'validation_error', 'Invalid import file', issues);
  return { schemaVersion: envelope.schemaVersion, exportedAt: envelope.exportedAt, docs };
}

export type ImportResult = ReplaceResult;

/**
 * Replaces all data (audit log merged), records a single `import` audit entry and rebuilds the
 * points ledger from the imported occurrences (ADR-0011), which also fills in the points of an
 * older file. The rebuild writes its own summary entry when it changed anything.
 */
export async function importData(
  ctx: AuditContext,
  parsed: ParsedImport,
  reconcile: typeof reconcilePoints = reconcilePoints,
): Promise<ImportResult> {
  const result = await replaceAllCollections(ctx.db, parsed.docs);
  await record(ctx, {
    entity: 'import',
    entityId: new ObjectId(),
    action: 'create',
    after: { ...result.replaced, auditAdded: result.auditAdded, removedPointEntries: result.removedPointEntries },
    meta: { mode: 'replace', schemaVersion: parsed.schemaVersion, exportedAt: parsed.exportedAt },
  });
  // The data is already replaced and audited: a failing rebuild is logged, and the nightly run repeats it.
  await reconcilePointsSafely(ctx, 'import', reconcile);
  return result;
}

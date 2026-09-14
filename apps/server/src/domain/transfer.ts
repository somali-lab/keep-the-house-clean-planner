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

export const EXPORT_SCHEMA_VERSION = 1;

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
  schemaVersion: z.literal(EXPORT_SCHEMA_VERSION),
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

export async function buildExport(db: Db, now: Date): Promise<ExportFile> {
  const docs = await readAllCollections(db);
  return {
    schemaVersion: EXPORT_SCHEMA_VERSION,
    exportedAt: now.toISOString(),
    collections: BSON.EJSON.serialize(docs, { relaxed: true }) as ExportFile['collections'],
  };
}

export interface ParsedImport {
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
  if (issues.length > 0) throw new HttpError(400, 'validation_error', 'Invalid import file', issues);
  return { exportedAt: envelope.exportedAt, docs };
}

export type ImportResult = ReplaceResult;

/** Replaces all data (audit log merged) and records a single `import` audit entry. */
export async function importData(ctx: AuditContext, parsed: ParsedImport): Promise<ImportResult> {
  const result = await replaceAllCollections(ctx.db, parsed.docs);
  await record(ctx, {
    entity: 'import',
    entityId: new ObjectId(),
    action: 'create',
    after: { ...result.replaced, auditAdded: result.auditAdded },
    meta: { mode: 'replace', schemaVersion: EXPORT_SCHEMA_VERSION, exportedAt: parsed.exportedAt },
  });
  return result;
}

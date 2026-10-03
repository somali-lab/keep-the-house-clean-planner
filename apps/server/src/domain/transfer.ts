import {
  auditEntrySchema,
  badgeRuleSchema,
  BADGE_IMAGE_TYPES,
  MAX_BADGE_DESCRIPTION_LENGTH,
  MAX_BADGE_IMAGE_BYTES,
  MAX_BADGE_NAME_LENGTH,
  centsPerPointSchema,
  currencyCodeSchema,
  cyclePlanSchema,
  cycleSchema,
  isoDateTimeSchema,
  MAX_REDEMPTION_NOTE_LENGTH,
  objectIdSchema,
  occurrenceSchema,
  pointEntrySourceSchema,
  requestKeySchema,
  roomSchema,
  settingsSchema,
  sniffBadgeImageType,
  taskSchema,
  toDayKey,
  userSchema,
} from '@huishoudplanner/shared';
import { createHash } from 'node:crypto';
import { BSON, Binary, ObjectId, type Db, type Document } from 'mongodb';
import { z } from 'zod';
import type { AuditContext } from '../audit/context.ts';
import { record } from '../audit/record.ts';
import { countBadges } from '../data/badges.ts';
import { countRedemptions } from '../data/points.ts';
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
 * `taskId` (ADR-0009). Version 3 adds `tasks.points`, `occurrences.pointsSnapshot` and `occurrences.pointsOverride`; the points
 * ledger itself is not exported but rebuilt on import (ADR-0011). Version 4 adds the bonus schedule
 * to the settings (ADR-0012); a file without it rebuilds without bonuses. An import also accepts
 * versions 1 to 3, which are valid unchanged because the new fields are optional; the rebuild fills them in.
 * Version 5 adds the redemptions, in `collections.pointEntries`: they are booked, so unlike the derived
 * entries they cannot be rebuilt (requirements 4.12). Settings gain `currencyCode` and `centsPerPoint`. Files of
 * versions 1 to 4 have no redemptions and import without any.
 * Version 6 adds the badge definitions with their images, in `collections.badges`; the awards are derived and
 * rebuilt on import (ADR-0014). A file of versions 1 to 5 has no badges and imports without any, like every other
 * replaced collection.
 */
export const EXPORT_SCHEMA_VERSION = 6;

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

const envelopeSchema = z
  .object({
    schemaVersion: z.union([z.literal(1), z.literal(2), z.literal(3), z.literal(4), z.literal(5), z.literal(6)]),
    exportedAt: isoDateTimeSchema,
    collections: z.object({
      settings: rawDocs,
      users: rawDocs,
      rooms: rawDocs,
      tasks: rawDocs,
      cyclePlans: rawDocs,
      cycles: rawDocs,
      occurrences: rawDocs,
      // Only from version 5; an older file has no booked entries.
      pointEntries: rawDocs.optional(),
      // Only from version 6; an older file has no badges.
      badges: rawDocs.optional(),
      auditLog: rawDocs,
    }),
  })
  .superRefine((envelope, ctx) => {
    // A version 5 file without its redemptions would silently drop them on import.
    if (envelope.schemaVersion >= 5 && envelope.collections.pointEntries === undefined) {
      ctx.addIssue({ code: 'custom', path: ['collections', 'pointEntries'], message: 'required' });
    }
    // The same for a version 6 file without its badges.
    if (envelope.schemaVersion >= 6 && envelope.collections.badges === undefined) {
      ctx.addIssue({ code: 'custom', path: ['collections', 'badges'], message: 'required' });
    }
  });

/** A redemption in API form (requirements 4.12): the only kind of ledger entry that travels in an export. */
const redemptionDocSchema = z.object({
  _id: objectIdSchema,
  key: z.string().regex(/^redemption:[0-9a-f]{24}$/, 'invalid_redemption_key'),
  kind: z.literal('redemption'),
  personId: objectIdSchema,
  amount: z.number().int().max(-1),
  date: isoDateTimeSchema,
  weekStart: isoDateTimeSchema,
  periodStart: z.null().optional(),
  occurrenceId: z.null(),
  taskId: z.null(),
  titleSnapshot: z.string(),
  source: pointEntrySourceSchema,
  note: z.string().max(MAX_REDEMPTION_NOTE_LENGTH).nullable(),
  centsPerPointSnapshot: centsPerPointSchema,
  // Missing in a file from before the currency was kept with a booking; the household currency then applies.
  currencyCodeSnapshot: currencyCodeSchema.optional(),
  requestId: requestKeySchema.nullable(),
  createdAt: isoDateTimeSchema,
  updatedAt: isoDateTimeSchema,
});

/** A badge in API form (ADR-0014): its image bytes are base64 here, and a real Binary in the stored form. */
const badgeDocSchema = z.object({
  _id: objectIdSchema,
  name: z.string().trim().min(1).max(MAX_BADGE_NAME_LENGTH),
  description: z.string().max(MAX_BADGE_DESCRIPTION_LENGTH),
  rule: badgeRuleSchema,
  active: z.boolean(),
  exampleKey: z.string().min(1).max(100).nullable().optional(),
  image: z
    .object({
      data: z.string().min(1),
      contentType: z.enum(BADGE_IMAGE_TYPES),
      size: z.number().int().min(1).max(MAX_BADGE_IMAGE_BYTES),
      hash: z.string().regex(/^[0-9a-f]{64}$/),
    })
    .nullable()
    .optional(),
  createdAt: isoDateTimeSchema,
  updatedAt: isoDateTimeSchema,
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
  pointEntries: redemptionDocSchema,
  badges: badgeDocSchema,
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
    ids: ['taskId', 'cycleId', 'planId', 'assigneeId', 'periodOwnerId', 'completedBy'],
    dates: ['date', 'plannedDate', 'completedAt', ...TIMESTAMPS],
  },
  pointEntries: { ids: ['personId', 'occurrenceId', 'taskId'], dates: ['date', 'weekStart', ...TIMESTAMPS] },
  badges: { ids: [], dates: TIMESTAMPS },
  auditLog: { ids: ['actorId', 'entityId'], dates: ['at'] },
};

/** Id fields that may be missing from a file (ADR-0012: a missing period owner means the assignee). */
const OPTIONAL_ID_FIELDS = new Set(['periodOwnerId']);

function typeIssues(name: TransferCollection, doc: Document, path: string): FieldIssue[] {
  const issues: FieldIssue[] = [];
  const expectId = (value: unknown, field: string, nullable: boolean) => {
    if (!(value instanceof ObjectId) && !(nullable && value === null)) issues.push({ field, message: 'expected_object_id' });
  };
  expectId(doc._id, `${path}._id`, false);
  for (const field of TYPED_FIELDS[name].ids) {
    // Fields that older files do not have are optional; when present they must be real ids.
    if (doc[field] === undefined && OPTIONAL_ID_FIELDS.has(field)) continue;
    expectId(doc[field], `${path}.${field}`, true);
  }
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

/**
 * Redemptions that cannot be put back: one for a person who is not in the file, or one that would
 * violate a unique index (ledger key, requestId). Replacing the data deletes first and inserts after,
 * so such a file has to be refused before anything is written.
 */
function redemptionIssues(redemptions: Document[], users: Document[]): FieldIssue[] {
  const issues: FieldIssue[] = [];
  const people = new Set(users.map((user) => String(user._id)));
  const keys = new Set<string>();
  const requestIds = new Set<string>();
  redemptions.forEach((doc, i) => {
    const path = `collections.pointEntries.${i}`;
    if (!people.has(String(doc.personId))) issues.push({ field: `${path}.personId`, message: 'unknown_user' });
    if (keys.has(doc.key)) issues.push({ field: `${path}.key`, message: 'duplicate_key' });
    keys.add(doc.key);
    if (typeof doc.requestId === 'string') {
      if (requestIds.has(doc.requestId)) issues.push({ field: `${path}.requestId`, message: 'duplicate_request_id' });
      requestIds.add(doc.requestId);
    }
  });
  return issues;
}

/**
 * Brings the badges of a file in line with its tasks (ADR-0014): a task the file does not have (it was deleted before an
 * older export) is dropped from the rule, the tasks of a rule are put in a stable order, and a rule that named tasks and
 * names none left is deactivated, because an empty list would count every task.
 */
function reconcileBadgeTasks(badges: Document[], tasks: Document[]): Document[] {
  const known = new Set(tasks.map((task) => String(task._id)));
  return badges.map((doc) => {
    const rule = doc.rule as Document | undefined;
    if (!rule || !Array.isArray(rule.taskIds) || !rule.taskIds.every((id: unknown) => id instanceof ObjectId)) return doc;
    const kept = (rule.taskIds as ObjectId[]).filter((id) => known.has(String(id))).sort((a, b) => (a.toHexString() < b.toHexString() ? -1 : 1));
    const emptied = rule.taskIds.length > 0 && kept.length === 0;
    return { ...doc, rule: { ...rule, taskIds: kept }, ...(emptied ? { active: false } : {}) };
  });
}

/**
 * Badges that cannot be put back: a rule with a task id that is not an id, an image whose bytes are not
 * what the file says they are (size, hash, a real PNG, JPEG or WebP of the declared type; an SVG is refused), or a
 * duplicate example key, which would violate a unique index. Replacing the data deletes first and inserts after, so
 * such a file has to be refused before anything is written (ADR-0014).
 */
function badgeIssues(badges: Document[]): FieldIssue[] {
  const issues: FieldIssue[] = [];
  const exampleKeys = new Set<string>();
  badges.forEach((doc, i) => {
    const path = `collections.badges.${i}`;
    const rule = doc.rule as Document;
    if (Array.isArray(rule.taskIds)) {
      rule.taskIds.forEach((id: unknown, j: number) => {
        if (!(id instanceof ObjectId)) issues.push({ field: `${path}.rule.taskIds.${j}`, message: 'expected_object_id' });
      });
    }
    if (typeof doc.exampleKey === 'string') {
      if (exampleKeys.has(doc.exampleKey)) issues.push({ field: `${path}.exampleKey`, message: 'duplicate_example_key' });
      exampleKeys.add(doc.exampleKey);
    }
    const image = doc.image as Document | null | undefined;
    if (image) {
      if (!(image.data instanceof Binary)) {
        issues.push({ field: `${path}.image.data`, message: 'expected_binary' });
        return;
      }
      const bytes = Buffer.from(image.data.buffer.subarray(0, image.data.position));
      if (bytes.length !== image.size) issues.push({ field: `${path}.image.size`, message: 'image_size_mismatch' });
      if (createHash('sha256').update(bytes).digest('hex') !== image.hash) issues.push({ field: `${path}.image.hash`, message: 'image_hash_mismatch' });
      const type = sniffBadgeImageType(bytes);
      if (!type) issues.push({ field: `${path}.image.data`, message: 'unsupported_image_type' });
      else if (type !== image.contentType) issues.push({ field: `${path}.image.contentType`, message: 'image_type_mismatch' });
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

/**
 * The bonus schedule only holds rows that already apply: a future row would be applied by a later
 * run without anybody having set it, and a floor in the future would hide periods that ended. Both are
 * judged against today in the timezone of the file's settings.
 */
function bonusScheduleIssues(settings: Document | undefined, now: Date): FieldIssue[] {
  if (!settings || typeof settings.timezone !== 'string') return [];
  let todayKey: string;
  try {
    todayKey = toDayKey(now, settings.timezone);
  } catch {
    return [];
  }
  const issues: FieldIssue[] = [];
  if (Array.isArray(settings.bonusSchedule)) {
    settings.bonusSchedule.forEach((row: Document, i: number) => {
      if (typeof row?.from === 'string' && row.from > todayKey) {
        issues.push({ field: `collections.settings.0.bonusSchedule.${i}.from`, message: 'bonus_schedule_in_future' });
      }
    });
  }
  if (typeof settings.bonusFloor === 'string' && settings.bonusFloor > todayKey) {
    issues.push({ field: 'collections.settings.0.bonusFloor', message: 'bonus_floor_in_future' });
  }
  return issues;
}

/** Validates the whole file before anything is written; throws 400 validation_error listing every issue. */
export function parseImport(body: unknown, now: Date = new Date()): ParsedImport {
  const envelope = parseOrThrow(envelopeSchema, body);
  let deserialized: Record<TransferCollection, Document[]>;
  try {
    deserialized = BSON.EJSON.deserialize({ ...envelope.collections, pointEntries: envelope.collections.pointEntries ?? [], badges: envelope.collections.badges ?? [] }, { relaxed: true }) as Record<
      TransferCollection,
      Document[]
    >;
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
  issues.push(...bonusScheduleIssues(docs.settings[0], now));
  // The keys are built from typed values, so check them only once every document has the right types.
  if (issues.length === 0) {
    docs.badges = reconcileBadgeTasks(docs.badges, docs.tasks);
    issues.push(...duplicateKeyIssues(docs.occurrences), ...redemptionIssues(docs.pointEntries, docs.users), ...badgeIssues(docs.badges));
  }
  if (issues.length > 0) throw new HttpError(400, 'validation_error', 'Invalid import file', issues);
  return { schemaVersion: envelope.schemaVersion, exportedAt: envelope.exportedAt, docs };
}

export type ImportResult = ReplaceResult;

/**
 * The redemptions an import would remove without bringing any back: a file of version 4 or older has none
 * (requirements 4.12). Zero for a version 5 file, which carries its own redemptions and replaces them like any other collection.
 */
export async function redemptionsLostByImport(db: Db, parsed: ParsedImport): Promise<number> {
  return parsed.schemaVersion < 5 ? countRedemptions(db) : 0;
}

/**
 * The badges an import would remove without bringing any back: a file older than version 6 has none (ADR-0014). Zero for
 * a version 6 file, which carries its own badges and replaces them like any other collection.
 */
export async function badgesLostByImport(db: Db, parsed: ParsedImport): Promise<number> {
  return parsed.schemaVersion < 6 ? countBadges(db) : 0;
}

/**
 * Replaces all data (audit log merged), records a single `import` audit entry and rebuilds the
 * derived points ledger from the imported occurrences (ADR-0011), which also fills in the points of an
 * older file; the imported redemptions are kept (requirements 4.12). The rebuild writes its own summary entry
 * when it changed anything.
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
    after: {
      ...result.replaced,
      auditAdded: result.auditAdded,
      removedPointEntries: result.removedPointEntries,
      removedRedemptions: result.removedRedemptions,
      removedBadges: result.removedBadges,
      removedBadgeAwards: result.removedBadgeAwards,
    },
    meta: { mode: 'replace', schemaVersion: parsed.schemaVersion, exportedAt: parsed.exportedAt },
  });
  // The data is already replaced and audited: a failing rebuild is logged, and the nightly run repeats it.
  await reconcilePointsSafely(ctx, 'import', reconcile);
  return result;
}

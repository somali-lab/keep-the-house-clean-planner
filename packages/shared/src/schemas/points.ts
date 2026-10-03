import { z } from 'zod';
import { dayKeySchema, isoDateTimeSchema, objectIdSchema } from './common.ts';
import { MAX_TASK_POINTS, MIN_TASK_POINTS } from '../points.ts';

/** Points value of a task: an integer from 0 to 100; 0 means the task earns no points (ADR-0011). */
export const taskPointsSchema = z.number().int().min(MIN_TASK_POINTS).max(MAX_TASK_POINTS);

export const pointEntryKindSchema = z.enum(['execution']);
export type PointEntryKind = z.infer<typeof pointEntryKindSchema>;

/** The path that wrote the current value of a ledger entry. */
export const pointEntrySourceSchema = z.enum(['live', 'backfill', 'recompute']);
export type PointEntrySource = z.infer<typeof pointEntrySourceSchema>;

/** Why a ledger entry was created, changed or removed; recorded in the audit meta. */
export const pointsSyncReasonSchema = z.enum(['complete', 'recorded', 'uncomplete', 'retract', 'correction']);
export type PointsSyncReason = z.infer<typeof pointsSyncReasonSchema>;

/** API view of one ledger entry: ids, day keys and ISO instants (ADR-0002, ADR-0011). */
export const pointEntryViewSchema = z.object({
  _id: objectIdSchema,
  key: z.string(),
  kind: pointEntryKindSchema,
  personId: objectIdSchema,
  amount: z.number().int(),
  date: dayKeySchema,
  weekStart: dayKeySchema,
  occurrenceId: objectIdSchema.nullable(),
  taskId: objectIdSchema.nullable(),
  titleSnapshot: z.string(),
  source: pointEntrySourceSchema,
  createdAt: isoDateTimeSchema,
  updatedAt: isoDateTimeSchema,
});
export type PointEntryView = z.infer<typeof pointEntryViewSchema>;

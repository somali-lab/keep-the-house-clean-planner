import { z } from 'zod';
import { dayKeySchema, isoDateTimeSchema, objectIdSchema } from './common.ts';
import { MAX_TASK_POINTS, MIN_TASK_POINTS } from '../points.ts';
import { daysBetween, isDayKey } from '../time.ts';

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

/** The audit summary and the recompute answer list at most this many corrections. */
export const MAX_POINTS_CORRECTIONS = 100;

/** Longest range, in calendar days, of one request for a person's ledger entries (53 weeks). */
export const MAX_POINTS_ENTRIES_RANGE_DAYS = 371;

/** Rejects a range that ends before it starts; shared by the balances and the entries query. */
function refineRange(value: { from?: string | undefined; to?: string | undefined }, ctx: z.RefinementCtx): void {
  if (value.from !== undefined && value.to !== undefined && isDayKey(value.from) && isDayKey(value.to) && value.from > value.to) {
    ctx.addIssue({ code: 'custom', path: ['from'], message: 'from_after_to' });
  }
}

/** Both days are optional; without them the balances cover the whole ledger. */
export const pointsBalancesQuerySchema = z
  .object({ from: dayKeySchema.optional(), to: dayKeySchema.optional() })
  .superRefine(refineRange);
export type PointsBalancesQuery = z.infer<typeof pointsBalancesQuerySchema>;

/** One person's entries; the range is required and spans at most 371 days, both days included. */
export const pointsEntriesQuerySchema = z
  .object({ personId: objectIdSchema, from: dayKeySchema, to: dayKeySchema })
  .superRefine((value, ctx) => {
    refineRange(value, ctx);
    if (isDayKey(value.from) && isDayKey(value.to) && daysBetween(value.from, value.to) + 1 > MAX_POINTS_ENTRIES_RANGE_DAYS) {
      ctx.addIssue({ code: 'custom', path: ['to'], message: 'range_too_large' });
    }
  });
export type PointsEntriesQuery = z.infer<typeof pointsEntriesQuerySchema>;

export const personBalanceSchema = z.object({
  personId: objectIdSchema,
  /** Sum of the entries in the range; can be negative once redemptions exist. */
  points: z.number().int(),
  /** Number of entries in the range. */
  executions: z.number().int().min(0),
});
export type PersonBalance = z.infer<typeof personBalanceSchema>;

export const pointsBalancesResponseSchema = z.object({
  from: dayKeySchema.nullable(),
  to: dayKeySchema.nullable(),
  /** Every active user, also at 0, and every inactive user with entries in the range; in the order of the user list. */
  balances: z.array(personBalanceSchema),
});
export type PointsBalancesResponse = z.infer<typeof pointsBalancesResponseSchema>;

export const pointsEntriesResponseSchema = z.object({
  /** Newest date first, then by id. */
  entries: z.array(pointEntryViewSchema),
});
export type PointsEntriesResponse = z.infer<typeof pointsEntriesResponseSchema>;

/** What started a reconciliation of the ledger with the occurrences (ADR-0011). */
export const pointsRecomputeTriggerSchema = z.enum(['startup', 'nightly', 'import', 'admin']);
export type PointsRecomputeTrigger = z.infer<typeof pointsRecomputeTriggerSchema>;

/** One ledger entry that was changed or removed because it had drifted from its occurrence. */
export const pointsCorrectionSchema = z.object({
  key: z.string(),
  from: z.object({ personId: objectIdSchema, amount: z.number().int() }),
  /** Null when the entry was removed. */
  to: z.object({ personId: objectIdSchema, amount: z.number().int() }).nullable(),
});
export type PointsCorrection = z.infer<typeof pointsCorrectionSchema>;

export const pointsRecomputeResultSchema = z.object({
  trigger: pointsRecomputeTriggerSchema,
  /** Tasks that got the default points for their duration. */
  tasksDefaulted: z.number().int().min(0),
  /** Done occurrences that got a points snapshot. */
  snapshotsSet: z.number().int().min(0),
  created: z.number().int().min(0),
  updated: z.number().int().min(0),
  removed: z.number().int().min(0),
  /** Done occurrences with points but nobody to credit; they earn no entry. */
  unattributed: z.number().int().min(0),
  /** Done occurrences that could not be read (an invalid date, for example); they are left as they are. */
  skipped: z.number().int().min(0),
  /** The first corrections only, at most {@link MAX_POINTS_CORRECTIONS}; `correctionsTotal` counts all of them. */
  corrections: z.array(pointsCorrectionSchema).max(MAX_POINTS_CORRECTIONS),
  correctionsTotal: z.number().int().min(0),
  correctionsTruncated: z.boolean(),
});
export type PointsRecomputeResult = z.infer<typeof pointsRecomputeResultSchema>;

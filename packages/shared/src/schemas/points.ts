import { z } from 'zod';
import { dayKeySchema, isoDateTimeSchema, objectIdSchema } from './common.ts';
import { requestKeySchema } from './occurrences.ts';
import { BONUS_KINDS } from '../bonuses.ts';
import { MAX_CENTS_PER_POINT, MAX_REDEMPTION_NOTE_LENGTH, MAX_TASK_POINTS, MIN_TASK_POINTS } from '../points.ts';
import { daysBetween, isDayKey } from '../time.ts';

/** Points value of a task: an integer from 0 to 100; 0 means the task earns no points (ADR-0011). */
export const taskPointsSchema = z.number().int().min(MIN_TASK_POINTS).max(MAX_TASK_POINTS);

/** `execution` and the four bonus kinds are derived from the occurrences; `redemption` is booked by a person (requirements 4.12). */
export const pointEntryKindSchema = z.enum(['execution', ...BONUS_KINDS, 'redemption']);
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
  /** First day of the week or cycle a bonus is for; null for an execution (ADR-0012). */
  periodStart: dayKeySchema.nullable(),
  occurrenceId: objectIdSchema.nullable(),
  taskId: objectIdSchema.nullable(),
  titleSnapshot: z.string(),
  /** Free text of a redemption; null for a derived entry (requirements 4.12). */
  note: z.string().nullable(),
  /** Currency of the household when a redemption was booked; null for a derived entry (requirements 4.12). */
  currencyCodeSnapshot: z.string().regex(/^[A-Z]{3}$/).nullable(),
  /** Cents one point was worth when a redemption was booked; null for a derived entry (requirements 4.12). */
  centsPerPointSnapshot: z.number().int().min(0).max(MAX_CENTS_PER_POINT).nullable(),
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

/** The money values of a balance, in whole cents at the factor in force now; present only when points are worth money (requirements 4.12). */
export const balanceMoneySchema = z.object({
  earned: z.number().int(),
  redeemed: z.number().int(),
  balance: z.number().int(),
});
export type BalanceMoney = z.infer<typeof balanceMoneySchema>;

export const personBalanceSchema = z.object({
  personId: objectIdSchema,
  /** The balance: the sum of all entries in the range, so earned minus redeemed. Negative when work that was redeemed against is undone. */
  points: z.number().int(),
  /** Points of executions and bonuses in the range. */
  earned: z.number().int(),
  /** Points redeemed in the range, as a positive number (requirements 4.12). */
  redeemed: z.number().int().min(0),
  /** Money of earned, redeemed and the balance; null while a point is worth nothing. */
  money: balanceMoneySchema.nullable(),
  /** Number of entries of kind execution in the range. */
  executions: z.number().int().min(0),
  /** Sum of the week and cycle bonus entries in the range; included in `points`. */
  bonusPoints: z.number().int(),
});
export type PersonBalance = z.infer<typeof personBalanceSchema>;

export const pointsBalancesResponseSchema = z.object({
  from: dayKeySchema.nullable(),
  to: dayKeySchema.nullable(),
  /** ISO 4217 code of the household currency. */
  currencyCode: z.string(),
  /** Cents one point is worth now; 0 means no money is shown. */
  centsPerPoint: z.number().int().min(0).max(MAX_CENTS_PER_POINT),
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

/** One bonus entry that was created or removed by a reconciliation (ADR-0012). */
export const pointsBonusChangeSchema = z.object({
  key: z.string(),
  personId: objectIdSchema,
  amount: z.number().int(),
  change: z.enum(['created', 'removed']),
});
export type PointsBonusChange = z.infer<typeof pointsBonusChangeSchema>;

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
  /** Occurrences that could not be read (an invalid date, for example); they and the bonuses of their owner are left as they are. */
  skipped: z.number().int().min(0),
  /** The first corrections only, at most {@link MAX_POINTS_CORRECTIONS}; `correctionsTotal` counts all of them. */
  corrections: z.array(pointsCorrectionSchema).max(MAX_POINTS_CORRECTIONS),
  correctionsTotal: z.number().int().min(0),
  correctionsTruncated: z.boolean(),
  bonusesCreated: z.number().int().min(0),
  bonusesRemoved: z.number().int().min(0),
  /** The first created or removed bonus entries only, at most {@link MAX_POINTS_CORRECTIONS}; `bonusChangesTotal` counts all of them. */
  bonusChanges: z.array(pointsBonusChangeSchema).max(MAX_POINTS_CORRECTIONS),
  bonusChangesTotal: z.number().int().min(0),
  bonusChangesTruncated: z.boolean(),
});
export type PointsRecomputeResult = z.infer<typeof pointsRecomputeResultSchema>;

/** Books a redemption: the person (an administrator may name anyone, everybody else only themselves) gives up points (requirements 4.12). */
export const createRedemptionInputSchema = z.object({
  /** Defaults to the active profile. */
  personId: objectIdSchema.optional(),
  /** Points to redeem: at least 1 and at most the balance. */
  points: z.number().int().min(1),
  /** Optional remark, for example what the points were exchanged for. */
  note: z.string().trim().max(MAX_REDEMPTION_NOTE_LENGTH).optional(),
  /** Client idempotency key; a repeat of the same request replays the stored booking. */
  requestId: requestKeySchema.optional(),
});
export type CreateRedemptionInput = z.infer<typeof createRedemptionInputSchema>;

/** The periods of the reward meter: the calendar week or the four-week cycle of today (requirements 4.12). */
export const rewardPeriodSchema = z.enum(['week', 'cycle']);
export type RewardPeriod = z.infer<typeof rewardPeriodSchema>;

export const pointsProgressQuerySchema = z.object({ personId: objectIdSchema, period: rewardPeriodSchema });
export type PointsProgressQuery = z.infer<typeof pointsProgressQuerySchema>;

/** Progress of one person towards the goal of the current week or cycle (requirements 4.12). */
export const pointsProgressResponseSchema = z.object({
  personId: objectIdSchema,
  period: rewardPeriodSchema,
  /** First and last day of the period, both included. */
  start: dayKeySchema,
  end: dayKeySchema,
  /** Points of executions and bonuses dated in the period; redemptions do not reduce it. */
  earnedPoints: z.number().int().min(0),
  /** Null when there is no goal: nothing planned for the person and no goal set, or a goal of 0. */
  goalPoints: z.number().int().min(1).nullable(),
  goalSource: z.enum(['explicit', 'automatic']),
  /** 0 to 100, capped; 0 without a goal. */
  percent: z.number().int().min(0).max(100),
  currencyCode: z.string(),
  /** Cents one point is worth now; 0 means no money is shown. */
  centsPerPoint: z.number().int().min(0).max(MAX_CENTS_PER_POINT),
  /** Earned points and the goal in cents at the factor in force now; null while a point is worth nothing. */
  money: z.object({ earned: z.number().int().min(0), goal: z.number().int().min(1).nullable() }).nullable(),
});
export type PointsProgressResponse = z.infer<typeof pointsProgressResponseSchema>;

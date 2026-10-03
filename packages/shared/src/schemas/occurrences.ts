import { z } from 'zod';
import { dayKeySchema, isoDateTimeSchema, objectIdSchema, timestampsSchema } from './common.ts';

export const occurrenceStatusSchema = z.enum(['open', 'done', 'skipped']);
export type OccurrenceStatus = z.infer<typeof occurrenceStatusSchema>;

export const occurrenceOriginSchema = z.enum(['generated', 'adhoc']);

/** Client idempotency key of an ad-hoc creation (ADR-0009). */
export const requestKeySchema = z.string().regex(/^[A-Za-z0-9_-]{16,64}$/, 'invalid_request_key');

export const occurrenceSchema = z
  .object({
    _id: objectIdSchema,
    /** Null for a one-off task (ADR-0009): name, duration and room live in the snapshot fields only. */
    taskId: objectIdSchema.nullable(),
    cycleId: objectIdSchema,
    planId: objectIdSchema.nullable(),
    date: dayKeySchema,
    plannedDate: dayKeySchema,
    assigneeId: objectIdSchema.nullable(),
    status: occurrenceStatusSchema,
    statusBeforeCompletion: z.enum(['open', 'skipped']).nullable(),
    completedAt: isoDateTimeSchema.nullable(),
    completedBy: objectIdSchema.nullable(),
    skipReason: z.string().nullable(),
    durationMinutesSnapshot: z.number().int().min(1),
    taskNameSnapshot: z.string(),
    roomIdSnapshot: objectIdSchema.nullable().optional(),
    roomNameSnapshot: z.string().nullable().optional(),
    origin: occurrenceOriginSchema,
    /** Created directly in the done state; has no planned state to return to. Missing on older data means false. */
    recordedDone: z.boolean().optional(),
    /** Idempotency key of the creating request. Missing on older data means null. */
    requestId: z.string().nullable().optional(),
    /** Points of this execution, fixed when it became done (ADR-0011). Null or missing means not yet snapshotted. */
    pointsSnapshot: z.number().int().min(0).nullable().optional(),
  })
  .extend(timestampsSchema.shape);
export type Occurrence = z.infer<typeof occurrenceSchema>;

export const occurrenceViewSchema = occurrenceSchema.extend({
  isOverdue: z.boolean(),
  movedFrom: dayKeySchema.nullable(),
});
export type OccurrenceView = z.infer<typeof occurrenceViewSchema>;

export const listOccurrencesQuerySchema = z.object({
  from: dayKeySchema,
  to: dayKeySchema,
  assigneeId: objectIdSchema.optional(),
  status: occurrenceStatusSchema.optional(),
});
export type ListOccurrencesQuery = z.infer<typeof listOccurrencesQuerySchema>;

export const createOccurrenceInputSchema = z.object({
  taskId: objectIdSchema,
  date: dayKeySchema,
  assigneeId: objectIdSchema.nullable().optional(),
  /** Create the occurrence already done; only allowed for today. */
  done: z.boolean().optional(),
  requestId: requestKeySchema.optional(),
});
export type CreateOccurrenceInput = z.infer<typeof createOccurrenceInputSchema>;

export const createOneOffOccurrenceInputSchema = z.object({
  name: z.string().trim().min(1).max(120),
  roomId: objectIdSchema.nullable().optional(),
  durationMinutes: z.number().int().min(1),
  date: dayKeySchema,
  assigneeId: objectIdSchema.nullable().optional(),
  /** Record the work as already done; only allowed for today. */
  done: z.boolean().optional(),
  requestId: requestKeySchema.optional(),
});
export type CreateOneOffOccurrenceInput = z.infer<typeof createOneOffOccurrenceInputSchema>;

export const patchOccurrenceInputSchema = z.discriminatedUnion('action', [
  z
    .object({
      action: z.literal('complete'),
      completedBy: objectIdSchema.optional(),
      takeOver: z.literal(true).optional(),
    })
    // Two choices at once make no sense: either the named person or the actor performed the work (ADR-0011).
    .refine((input) => !(input.completedBy !== undefined && input.takeOver === true), {
      path: ['completedBy'],
      message: 'completion_choice_conflict',
    }),
  z.object({ action: z.literal('uncomplete') }),
  z.object({
    action: z.literal('edit_completion'),
    date: dayKeySchema,
    completedAt: isoDateTimeSchema,
    completedBy: objectIdSchema,
  }),
  z.object({ action: z.literal('skip'), reason: z.string().trim().max(500).optional() }),
  z.object({ action: z.literal('reschedule'), date: dayKeySchema }),
  z.object({ action: z.literal('assign'), assigneeId: objectIdSchema.nullable() }),
]);
export type PatchOccurrenceInput = z.infer<typeof patchOccurrenceInputSchema>;

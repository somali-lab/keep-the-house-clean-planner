import { z } from 'zod';
import { dayKeySchema, isoDateTimeSchema, objectIdSchema, timestampsSchema } from './common.ts';

export const occurrenceStatusSchema = z.enum(['open', 'done', 'skipped']);
export type OccurrenceStatus = z.infer<typeof occurrenceStatusSchema>;

export const occurrenceOriginSchema = z.enum(['generated', 'adhoc']);

export const occurrenceSchema = z
  .object({
    _id: objectIdSchema,
    taskId: objectIdSchema,
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
});
export type CreateOccurrenceInput = z.infer<typeof createOccurrenceInputSchema>;

export const patchOccurrenceInputSchema = z.discriminatedUnion('action', [
  z.object({ action: z.literal('complete'), completedBy: objectIdSchema.optional() }),
  z.object({ action: z.literal('uncomplete') }),
  z.object({ action: z.literal('skip'), reason: z.string().trim().max(500).optional() }),
  z.object({ action: z.literal('reschedule'), date: dayKeySchema }),
  z.object({ action: z.literal('assign'), assigneeId: objectIdSchema.nullable() }),
]);
export type PatchOccurrenceInput = z.infer<typeof patchOccurrenceInputSchema>;

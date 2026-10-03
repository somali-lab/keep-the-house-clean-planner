import { z } from 'zod';
import { isoDateTimeSchema, objectIdSchema, timestampsSchema } from './common.ts';
import { taskPointsSchema } from './points.ts';

export const taskSchema = z
  .object({
    _id: objectIdSchema,
    name: z.string().trim().min(1),
    roomId: objectIdSchema,
    intervalKey: z.string().min(1),
    durationMinutes: z.number().int().min(1),
    /** Points per execution (0..1000). Missing on older data means the default for the duration (ADR-0011). */
    points: taskPointsSchema.optional(),
    defaultAssigneeId: objectIdSchema.nullable(),
    active: z.boolean(),
    notes: z.string(),
    tags: z.array(z.string()),
    lastCompletedAt: isoDateTimeSchema.nullable(),
  })
  .extend(timestampsSchema.shape);
export type Task = z.infer<typeof taskSchema>;

export const createTaskInputSchema = z.object({
  name: z.string().trim().min(1),
  roomId: objectIdSchema,
  intervalKey: z.string().min(1),
  durationMinutes: z.number().int().min(1),
  /** Omitted: the server defaults it from the duration (one point per minute). */
  points: taskPointsSchema.optional(),
  defaultAssigneeId: objectIdSchema.nullable().default(null),
  notes: z.string().default(''),
  tags: z.array(z.string().trim().min(1)).default([]),
});
export type CreateTaskInput = z.infer<typeof createTaskInputSchema>;

export const updateTaskInputSchema = z
  .object({
    name: z.string().trim().min(1),
    roomId: objectIdSchema,
    intervalKey: z.string().min(1),
    durationMinutes: z.number().int().min(1),
    points: taskPointsSchema,
    defaultAssigneeId: objectIdSchema.nullable(),
    active: z.boolean(),
    notes: z.string(),
    tags: z.array(z.string().trim().min(1)),
  })
  .partial();
export type UpdateTaskInput = z.infer<typeof updateTaskInputSchema>;

export const bulkRoomTasksInputSchema = z.discriminatedUnion('op', [
  z.object({ op: z.literal('deactivate') }),
  z.object({ op: z.literal('reassign'), defaultAssigneeId: objectIdSchema.nullable() }),
]);
export type BulkRoomTasksInput = z.infer<typeof bulkRoomTasksInputSchema>;

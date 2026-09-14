import { z } from 'zod';
import { isoDateTimeSchema, objectIdSchema, timestampsSchema } from './common.ts';

export const taskSchema = z
  .object({
    _id: objectIdSchema,
    name: z.string().trim().min(1),
    roomId: objectIdSchema,
    intervalKey: z.string().min(1),
    durationMinutes: z.number().int().min(1),
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

import { z } from 'zod';
import { objectIdSchema, timestampsSchema } from './common.ts';

export const roomSchema = z
  .object({
    _id: objectIdSchema,
    name: z.string().trim().min(1),
    sortOrder: z.number().int(),
    active: z.boolean(),
    virtual: z.boolean(),
  })
  .extend(timestampsSchema.shape);
export type Room = z.infer<typeof roomSchema>;

export const createRoomInputSchema = z.object({
  name: z.string().trim().min(1),
  sortOrder: z.number().int().optional(),
  virtual: z.boolean().default(false),
});
export type CreateRoomInput = z.infer<typeof createRoomInputSchema>;

export const updateRoomInputSchema = z
  .object({
    name: z.string().trim().min(1),
    sortOrder: z.number().int(),
    active: z.boolean(),
    virtual: z.boolean(),
  })
  .partial();
export type UpdateRoomInput = z.infer<typeof updateRoomInputSchema>;

import { z } from 'zod';
import { objectIdSchema, timestampsSchema, weekdaySchema, weekIndexSchema } from './common.ts';

export const slotSchema = z.object({
  taskId: objectIdSchema,
  weekIndex: weekIndexSchema,
  weekday: weekdaySchema,
  assigneeId: objectIdSchema.nullable(),
  sortOrder: z.number().int().default(0),
});
export type Slot = z.infer<typeof slotSchema>;

export const weekThemesSchema = z.tuple([z.string(), z.string(), z.string(), z.string()]);

export const planSourceSchema = z.enum(['manual', 'ai']);

export const cyclePlanSchema = z
  .object({
    _id: objectIdSchema,
    name: z.string().trim().min(1),
    active: z.boolean(),
    slots: z.array(slotSchema),
    weekThemes: weekThemesSchema,
    draft: z.boolean(),
    source: planSourceSchema,
    proposalId: z.string().nullable(),
    rationale: weekThemesSchema.nullable(),
    discarded: z.boolean(),
  })
  .extend(timestampsSchema.shape);
export type CyclePlan = z.infer<typeof cyclePlanSchema>;

export const createCyclePlanInputSchema = z.object({
  name: z.string().trim().min(1),
  copyFromId: objectIdSchema.optional(),
});
export type CreateCyclePlanInput = z.infer<typeof createCyclePlanInputSchema>;

export const updateCyclePlanInputSchema = z
  .object({
    name: z.string().trim().min(1),
    weekThemes: weekThemesSchema,
  })
  .partial();
export type UpdateCyclePlanInput = z.infer<typeof updateCyclePlanInputSchema>;

export const putSlotsInputSchema = z.object({
  slots: z.array(slotSchema),
});
export type PutSlotsInput = z.infer<typeof putSlotsInputSchema>;

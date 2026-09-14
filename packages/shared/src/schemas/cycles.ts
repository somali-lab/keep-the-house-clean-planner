import { z } from 'zod';
import { dayKeySchema, isoDateTimeSchema, objectIdSchema } from './common.ts';

export const cycleSchema = z.object({
  _id: objectIdSchema,
  index: z.number().int(),
  startDate: dayKeySchema,
  endDate: dayKeySchema,
  planId: objectIdSchema.nullable(),
  generatedAt: isoDateTimeSchema,
  generationRunId: z.string(),
});
export type Cycle = z.infer<typeof cycleSchema>;

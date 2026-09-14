import { z } from 'zod';
import { objectIdSchema, weekdaySchema, weekIndexSchema } from './common.ts';

/** Which template slot moves to which weekday. */
export const promotionIdentitySchema = z.object({
  planId: objectIdSchema,
  taskId: objectIdSchema,
  weekIndex: weekIndexSchema,
  weekday: weekdaySchema,
  toWeekday: weekdaySchema,
});

export const applyPromotionInputSchema = promotionIdentitySchema.extend({
  /** Omitted = keep the slot's assignee. */
  toAssigneeId: objectIdSchema.optional(),
});
export type ApplyPromotionInput = z.infer<typeof applyPromotionInputSchema>;

export interface PromoteSuggestion {
  planId: string;
  taskId: string;
  taskName: string;
  fromSlot: { weekIndex: number; weekday: number; assigneeId: string | null };
  toWeekday: number;
  /** Present only when every move also went to this same other person. */
  toAssigneeId?: string;
  /** Occurrence ids, newest cycle first. */
  evidence: string[];
}

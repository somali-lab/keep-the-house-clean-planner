import { z } from 'zod';
import { hexColorSchema, objectIdSchema, timestampsSchema, weekdaySchema } from './common.ts';

export const dailyBudgetSchema = z.object({
  weekday: z.number().int().min(0),
  weekend: z.number().int().min(0),
});

export const userSchema = z
  .object({
    _id: objectIdSchema,
    name: z.string().trim().min(1),
    color: hexColorSchema,
    active: z.boolean(),
    unavailableWeekdays: z.array(weekdaySchema),
    dailyBudgetMinutes: dailyBudgetSchema,
    maxDailyMinutes: dailyBudgetSchema.default({ weekday: 480, weekend: 480 }),
  })
  .extend(timestampsSchema.shape);
export type User = z.infer<typeof userSchema>;

export const createUserInputSchema = z.object({
  name: z.string().trim().min(1),
  color: hexColorSchema,
  unavailableWeekdays: z.array(weekdaySchema).default([]),
  dailyBudgetMinutes: dailyBudgetSchema.default({ weekday: 60, weekend: 120 }),
  maxDailyMinutes: dailyBudgetSchema.default({ weekday: 60, weekend: 120 }),
});
export type CreateUserInput = z.infer<typeof createUserInputSchema>;

export const updateUserInputSchema = z
  .object({
    name: z.string().trim().min(1),
    color: hexColorSchema,
    active: z.boolean(),
    unavailableWeekdays: z.array(weekdaySchema),
    dailyBudgetMinutes: dailyBudgetSchema,
    maxDailyMinutes: dailyBudgetSchema,
  })
  .partial();
export type UpdateUserInput = z.infer<typeof updateUserInputSchema>;

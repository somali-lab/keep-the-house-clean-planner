import { z } from 'zod';
import { hexColorSchema, objectIdSchema, timestampsSchema, weekdaySchema } from './common.ts';

export const dailyBudgetSchema = z.object({
  weekday: z.number().int().min(0),
  weekend: z.number().int().min(0),
});

export const MAX_BROWSER_NOTIFICATION_TIMES = 6;

/** 'HH:mm' on a 24-hour clock, read in the household timezone. */
export const notificationTimeSchema = z.string().regex(/^([01]\d|2[0-3]):[0-5]\d$/, 'invalid_time');

const uniqueTimes = (times: string[]) => new Set(times).size === times.length;

/**
 * Per-person moments for browser notifications (ADR-0010). Stored sorted; the
 * server sorts on write, so clients may send them in any order.
 */
export const browserNotificationsSchema = z.object({
  enabled: z.boolean(),
  times: z.array(notificationTimeSchema).max(MAX_BROWSER_NOTIFICATION_TIMES).refine(uniqueTimes, 'duplicate_time'),
});
export type BrowserNotifications = z.infer<typeof browserNotificationsSchema>;

/** What a user without stored notification settings reads as. */
export const DEFAULT_BROWSER_NOTIFICATIONS: BrowserNotifications = { enabled: false, times: [] };

export const userRoleSchema = z.enum(['admin', 'planner', 'member']);
export type UserRole = z.infer<typeof userRoleSchema>;

export const userSchema = z
  .object({
    _id: objectIdSchema,
    name: z.string().trim().min(1),
    color: hexColorSchema,
    active: z.boolean(),
    role: userRoleSchema.default('member'),
    unavailableWeekdays: z.array(weekdaySchema),
    dailyBudgetMinutes: dailyBudgetSchema,
    maxDailyMinutes: dailyBudgetSchema.default({ weekday: 480, weekend: 480 }),
    browserNotifications: browserNotificationsSchema.default(DEFAULT_BROWSER_NOTIFICATIONS),
  })
  .extend(timestampsSchema.shape);
export type User = z.infer<typeof userSchema>;

export const createUserInputSchema = z.object({
  name: z.string().trim().min(1),
  color: hexColorSchema,
  role: userRoleSchema.default('member'),
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
    role: userRoleSchema,
    unavailableWeekdays: z.array(weekdaySchema),
    dailyBudgetMinutes: dailyBudgetSchema,
    maxDailyMinutes: dailyBudgetSchema,
    browserNotifications: browserNotificationsSchema,
  })
  .partial();
export type UpdateUserInput = z.infer<typeof updateUserInputSchema>;

/** Body of PUT /api/users/:id/browser-notifications: the complete setting replaces the stored one. */
export const updateBrowserNotificationsInputSchema = browserNotificationsSchema;
export type UpdateBrowserNotificationsInput = BrowserNotifications;

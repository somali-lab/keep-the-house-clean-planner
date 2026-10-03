import { z } from 'zod';
import {
  BADGE_IMAGE_TYPES,
  MAX_BADGE_DESCRIPTION_LENGTH,
  MAX_BADGE_IMAGE_BASE64_LENGTH,
  MAX_BADGE_IMAGE_BYTES,
  MAX_BADGE_NAME_LENGTH,
  MAX_BADGE_RULE_TASKS,
  MAX_BADGE_THRESHOLD,
  MAX_ON_TIME_WEEKS_THRESHOLD,
} from '../badges.ts';
import { isoDateTimeSchema, objectIdSchema, timestampsSchema } from './common.ts';

const taskIdsSchema = z.array(objectIdSchema).max(MAX_BADGE_RULE_TASKS);
const thresholdSchema = z.number().int().min(1).max(MAX_BADGE_THRESHOLD);

/** The rule of a badge (ADR-0014). An empty `taskIds` means every task, one-off tasks included. */
export const badgeRuleSchema = z.discriminatedUnion('type', [
  z.object({ type: z.literal('executions'), taskIds: taskIdsSchema, threshold: thresholdSchema }),
  z.object({ type: z.literal('minutes'), taskIds: taskIdsSchema, threshold: thresholdSchema }),
  z.object({ type: z.literal('onTimeWeeks'), threshold: z.number().int().min(1).max(MAX_ON_TIME_WEEKS_THRESHOLD) }),
]);

/** An uploaded image: base64 text of at most 256 KB of PNG, JPEG or WebP; the server checks the real bytes. */
export const badgeImageInputSchema = z.object({
  contentType: z.enum(BADGE_IMAGE_TYPES),
  data: z.string().min(1).max(MAX_BADGE_IMAGE_BASE64_LENGTH, 'image_too_large'),
});
export type BadgeImageInput = z.infer<typeof badgeImageInputSchema>;

const nameSchema = z.string().trim().min(1).max(MAX_BADGE_NAME_LENGTH);
const descriptionSchema = z.string().trim().max(MAX_BADGE_DESCRIPTION_LENGTH);

export const createBadgeInputSchema = z.object({
  name: nameSchema,
  description: descriptionSchema.default(''),
  rule: badgeRuleSchema,
  active: z.boolean().default(true),
  image: badgeImageInputSchema.optional(),
});
export type CreateBadgeInput = z.infer<typeof createBadgeInputSchema>;

/** `image: null` removes the image; a new `image` replaces it. */
export const updateBadgeInputSchema = z
  .object({
    name: nameSchema,
    description: descriptionSchema,
    rule: badgeRuleSchema,
    active: z.boolean(),
    image: badgeImageInputSchema.nullable(),
  })
  .partial();
export type UpdateBadgeInput = z.infer<typeof updateBadgeInputSchema>;

/** Where the stored image of a badge is served from, and what identifies its bytes. */
export const badgeImageViewSchema = z.object({
  contentType: z.enum(BADGE_IMAGE_TYPES),
  size: z.number().int().min(1).max(MAX_BADGE_IMAGE_BYTES),
  /** Hex SHA-256 of the bytes. */
  hash: z.string().regex(/^[0-9a-f]{64}$/),
  /** `/api/badges/:id/image?v=<hash prefix>`: a changed image has a new address, so it can be cached for good. */
  url: z.string(),
});
export type BadgeImageView = z.infer<typeof badgeImageViewSchema>;

export const badgeSchema = z
  .object({
    _id: objectIdSchema,
    name: nameSchema,
    description: z.string().max(MAX_BADGE_DESCRIPTION_LENGTH),
    rule: badgeRuleSchema,
    active: z.boolean(),
    /** Set on a badge that the "add example badges" action created. */
    exampleKey: z.string().nullable(),
    image: badgeImageViewSchema.nullable(),
  })
  .extend(timestampsSchema.shape);
export type Badge = z.infer<typeof badgeSchema>;

export const badgesResponseSchema = z.object({ badges: z.array(badgeSchema) });
export type BadgesResponse = z.infer<typeof badgesResponseSchema>;

export const badgeAwardSchema = z.object({
  _id: objectIdSchema,
  badgeId: objectIdSchema,
  personId: objectIdSchema,
  /** When the data first crossed the threshold; not when it was computed. */
  awardedAt: isoDateTimeSchema,
});
export type BadgeAward = z.infer<typeof badgeAwardSchema>;

export const badgeAwardsQuerySchema = z.object({ personId: objectIdSchema.optional() });
export type BadgeAwardsQuery = z.infer<typeof badgeAwardsQuerySchema>;

export const badgeAwardsResponseSchema = z.object({ awards: z.array(badgeAwardSchema) });
export type BadgeAwardsResponse = z.infer<typeof badgeAwardsResponseSchema>;

export const badgeProgressQuerySchema = z.object({ personId: objectIdSchema });
export type BadgeProgressQuery = z.infer<typeof badgeProgressQuerySchema>;

/** How far one person is towards one active badge. */
export const badgeProgressItemSchema = z.object({
  badgeId: objectIdSchema,
  /** Executions, minutes or on-time weeks so far; it can exceed the threshold. */
  current: z.number().int().min(0),
  threshold: z.number().int().min(1),
  /** Null while the badge is not earned. */
  awardedAt: isoDateTimeSchema.nullable(),
});
export type BadgeProgressItem = z.infer<typeof badgeProgressItemSchema>;

export const badgeProgressResponseSchema = z.object({
  personId: objectIdSchema,
  items: z.array(badgeProgressItemSchema),
});
export type BadgeProgressResponse = z.infer<typeof badgeProgressResponseSchema>;

export const addExampleBadgesInputSchema = z.object({ language: z.enum(['nl', 'en']).default('nl') });
export type AddExampleBadgesInput = z.infer<typeof addExampleBadgesInputSchema>;

export const addExampleBadgesResponseSchema = z.object({
  /** The examples this call created; the ones that already existed are left alone. */
  created: z.array(badgeSchema),
  skipped: z.number().int().min(0),
});
export type AddExampleBadgesResponse = z.infer<typeof addExampleBadgesResponseSchema>;

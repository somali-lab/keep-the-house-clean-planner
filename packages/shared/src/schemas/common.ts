import { z } from 'zod';
import { isDayKey } from '../time.ts';

export const objectIdSchema = z.string().regex(/^[0-9a-f]{24}$/i, 'invalid_object_id');
export type Id = z.infer<typeof objectIdSchema>;

export const dayKeySchema = z.string().refine(isDayKey, 'invalid_day_key');

export const isoDateTimeSchema = z.iso.datetime({ offset: true });

export const timestampsSchema = z.object({
  createdAt: isoDateTimeSchema,
  updatedAt: isoDateTimeSchema,
});

/** 0=Sunday..6=Saturday */
export const weekdaySchema = z.number().int().min(0).max(6);
export const weekIndexSchema = z.number().int().min(0).max(3);

export const hexColorSchema = z.string().regex(/^#[0-9a-f]{6}$/i, 'invalid_color');

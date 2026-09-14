import { z } from 'zod';

/** Error body for all non-2xx API responses. */
export const apiErrorSchema = z.object({
  code: z.string(),
  message: z.string().optional(),
  details: z.unknown().optional(),
});
export type ApiError = z.infer<typeof apiErrorSchema>;

export const apiWarningSchema = z.object({
  code: z.string(),
  message: z.string(),
  details: z.record(z.string(), z.unknown()).optional(),
});
export type ApiWarning = z.infer<typeof apiWarningSchema>;

/** Write responses that may carry non-blocking warnings. */
export interface WithWarnings<T> {
  data: T;
  warnings: ApiWarning[];
}

export const PROFILE_HEADER = 'x-profile-id';
export const CLIENT_HEADER = 'x-client';

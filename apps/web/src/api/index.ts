import { getActiveProfileId } from '../identity/profileStore.ts';
import { createApiClient } from './client.ts';
import { createV2Client } from './v2/client.ts';

/**
 * The client of the Node server (`/api`). Features move to `apiV2` one slice at a time (plan §8, phase 7); this
 * client goes away with the last of them.
 */
export const api = createApiClient({ getProfileId: getActiveProfileId });

/** The generated client of `/api/v2`; every request carries the active profile. */
export const apiV2 = createV2Client({ getProfileId: getActiveProfileId });

export { ApiRequestError, type ApiClient, type ApiResult } from './client.ts';
export { createV2Client, unwrap, type ApiV2Client, type ApiWarning, type V2Result } from './v2/client.ts';
export { toOccurrence, type Occurrence, type OccurrenceStatus } from './occurrence.ts';

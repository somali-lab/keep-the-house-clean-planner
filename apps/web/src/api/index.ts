import { getActiveProfileId } from '../identity/profileStore.ts';
import { createApiClient } from './client.ts';

/** Shared client; every request carries the active profile. */
export const api = createApiClient({ getProfileId: getActiveProfileId });

export { ApiRequestError, type ApiClient, type ApiResult } from './client.ts';

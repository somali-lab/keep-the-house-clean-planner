import { getActiveProfileId } from '../identity/profileStore.ts';
import { createV2Client } from './v2/client.ts';

/** The generated client of `/api/v2`; every request carries the active profile. */
export const apiV2 = createV2Client({ getProfileId: getActiveProfileId });

export { ApiRequestError } from './client.ts';
export { createV2Client, unwrap, type ApiV2Client, type ApiWarning, type V2Result } from './v2/client.ts';
export { toOccurrence, type Occurrence, type OccurrenceStatus } from './occurrence.ts';
export {
  etagOf,
  ifMatch,
  isStaleEntity,
  removeFromList,
  replaceInList,
  StaleEntityError,
  type Versioned,
} from './v2/concurrency.ts';

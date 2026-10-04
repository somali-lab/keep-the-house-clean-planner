import type { QueryClient, QueryKey } from '@tanstack/react-query';
import { ApiRequestError } from '../client.ts';

/**
 * Optimistic concurrency of the entity writes of API v2 (ADR-0022). An entity carries an integer `version`, and its ETag is the strong
 * validator `"<version>"`. A PATCH, PUT or DELETE on an entity sends the ETag of the entity the person was editing as `If-Match`; the
 * generated client types require the header, so a feature that forgets it does not compile. The web app holds no rule about versions
 * beyond this: it carries the number it read, and a `412` tells it that the entity has changed since.
 */

/** The problem code of a stale `If-Match`. */
export const PRECONDITION_FAILED = 'precondition_failed';

/** An entity as the web app lists it: the `version` of the document it was read at. */
export interface Versioned {
  version: number;
}

/** The strong validator of a version, as the server sends it in the `ETag` header: the number in double quotes. */
export const etagOf = (version: number): string => `"${version}"`;

/** The `header` parameter of a write on `entity`, for `params: { path, header: ifMatch(entity) }` of the generated client. */
export const ifMatch = (entity: Versioned): { 'If-Match': string } => ({ 'If-Match': etagOf(entity.version) });

/** The version in an `ETag` header value, or null when the header is missing or is not a strong validator of a version. */
export function versionOfEtag(etag: string | null | undefined): number | null {
  const match = /^"(\d+)"$/.exec(etag?.trim() ?? '');
  return match ? Number(match[1]) : null;
}

/**
 * The entity has changed since the person read it (`412 precondition_failed`): nothing was written. The UI re-reads the entity, keeps the
 * unsaved edit in its form and asks the person to review it and save again. `currentVersion` is the version now stored, when the
 * answer carried it.
 */
export class StaleEntityError extends ApiRequestError {
  readonly currentVersion: number | null;

  constructor(message: string, currentVersion: number | null, details?: unknown) {
    super(412, PRECONDITION_FAILED, message, details);
    this.name = 'StaleEntityError';
    this.currentVersion = currentVersion;
  }
}

export const isStaleEntity = (error: unknown): error is StaleEntityError => error instanceof StaleEntityError;

/**
 * Puts an entity that a write answered into a cached list, so that the version of the cache is the stored one and a second save needs no
 * re-read. Does nothing when the list is not cached; the caller still invalidates the query to pick up the rest of the server's state.
 */
export function replaceInList<T extends { id: string }>(queryClient: QueryClient, queryKey: QueryKey, entity: T): void {
  queryClient.setQueryData<T[] | undefined>(queryKey, (items) =>
    items?.map((item) => (item.id === entity.id ? entity : item)),
  );
}

/** Takes a deleted entity out of a cached list. */
export function removeFromList<T extends { id: string }>(queryClient: QueryClient, queryKey: QueryKey, id: string): void {
  queryClient.setQueryData<T[] | undefined>(queryKey, (items) => items?.filter((item) => item.id !== id));
}

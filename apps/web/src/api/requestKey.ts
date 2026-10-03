/** 64 symbols, so `byte & 63` maps a random byte to a symbol without bias. */
const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-';

/** Within the server's /^[A-Za-z0-9_-]{16,64}$/ idempotency-key rule (ADR-0009). */
export const REQUEST_KEY_LENGTH = 32;

type RandomFill = (bytes: Uint8Array<ArrayBuffer>) => Uint8Array;

/**
 * Creates an idempotency key for one user intent. It uses `crypto.getRandomValues`, which
 * (unlike `crypto.randomUUID`) also exists on the plain-HTTP household addresses the app
 * is served from. Callers keep the key for every retry of the same intent and create a new
 * one only after the request has settled.
 */
export function createRequestKey(fill: RandomFill = (bytes) => globalThis.crypto.getRandomValues(bytes)): string {
  const bytes = fill(new Uint8Array(REQUEST_KEY_LENGTH));
  let key = '';
  for (const byte of bytes) key += ALPHABET[byte & 63];
  return key;
}

/** Keys of requests that have not succeeded yet, by intent. Module-level, so they outlive a closed dialog or an unmounted page. */
const pendingKeys = new Map<string, string>();

/**
 * The key for one user intent (any string that identifies what is being requested). The same intent
 * gets the same key until `releaseRequestKey` is called, so a retry after a failure, a closed and
 * reopened dialog or a page that was left in between does not create the record twice.
 */
export function requestKeyFor(intent: string): string {
  let key = pendingKeys.get(intent);
  if (key === undefined) {
    key = createRequestKey();
    pendingKeys.set(intent, key);
  }
  return key;
}

/** Forgets the key of an intent once its request succeeded; a later identical action is a new intent. */
export function releaseRequestKey(intent: string): void {
  pendingKeys.delete(intent);
}

/** Test helper: forgets every pending key. */
export function resetRequestKeys(): void {
  pendingKeys.clear();
}

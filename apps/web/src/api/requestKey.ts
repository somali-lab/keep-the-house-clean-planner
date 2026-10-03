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

/**
 * How long a key that has not succeeded is kept. A retry or a reopened dialog within this time reuses it; a
 * deliberate identical action later (for example redeeming the same points again after a failed attempt that
 * was abandoned) gets a new key instead of being mistaken for the old one.
 */
export const REQUEST_KEY_TTL_MS = 10 * 60 * 1000;

/** Keys of requests that have not succeeded yet, by intent. Module-level, so they outlive a closed dialog or an unmounted page. */
const pendingKeys = new Map<string, { key: string; createdAt: number }>();

/**
 * The key for one user intent (any string that identifies what is being requested). The same intent
 * gets the same key until `releaseRequestKey` is called or {@link REQUEST_KEY_TTL_MS} has passed since the key
 * was made, so a retry after a failure, a closed and reopened dialog or a page that was left in between does
 * not create the record twice, while a deliberate later repeat is a new request.
 */
export function requestKeyFor(intent: string, now: number = Date.now()): string {
  const kept = pendingKeys.get(intent);
  if (kept !== undefined && now - kept.createdAt < REQUEST_KEY_TTL_MS) return kept.key;
  const key = createRequestKey();
  pendingKeys.set(intent, { key, createdAt: now });
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

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

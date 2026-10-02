import { requestKeySchema } from '@huishoudplanner/shared/schemas/occurrences';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { REQUEST_KEY_LENGTH, createRequestKey } from './requestKey.ts';

afterEach(() => vi.unstubAllGlobals());

describe('createRequestKey', () => {
  it('produces keys the server accepts', () => {
    for (let i = 0; i < 50; i++) {
      const key = createRequestKey();
      expect(key).toHaveLength(REQUEST_KEY_LENGTH);
      expect(requestKeySchema.safeParse(key).success).toBe(true);
    }
  });

  it('is derived from the random bytes, one symbol per byte', () => {
    expect(createRequestKey((bytes) => bytes.fill(0))).toBe('A'.repeat(REQUEST_KEY_LENGTH));
    expect(createRequestKey((bytes) => bytes.fill(63))).toBe('-'.repeat(REQUEST_KEY_LENGTH));
    // Only the low six bits count, so a full byte range still stays inside the alphabet.
    expect(createRequestKey((bytes) => bytes.fill(255))).toBe('-'.repeat(REQUEST_KEY_LENGTH));
    expect(createRequestKey((bytes) => bytes.map((_, i) => i))).toMatch(/^ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef$/);
  });

  it('gives a different key on each call', () => {
    const keys = new Set(Array.from({ length: 100 }, () => createRequestKey()));
    expect(keys.size).toBe(100);
  });

  it('works where crypto.randomUUID is unavailable (plain HTTP)', () => {
    const getRandomValues = <T extends ArrayBufferView>(array: T): T => {
      const bytes = new Uint8Array(array.buffer, array.byteOffset, array.byteLength);
      bytes.forEach((_, i) => (bytes[i] = (i * 7 + 3) & 255));
      return array;
    };
    vi.stubGlobal('crypto', { getRandomValues });
    expect(requestKeySchema.safeParse(createRequestKey()).success).toBe(true);
  });
});

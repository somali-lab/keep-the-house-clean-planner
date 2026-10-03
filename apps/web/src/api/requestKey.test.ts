import { requestKeySchema } from '@huishoudplanner/shared/schemas/occurrences';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { REQUEST_KEY_LENGTH, REQUEST_KEY_TTL_MS, createRequestKey, releaseRequestKey, requestKeyFor, resetRequestKeys } from './requestKey.ts';

afterEach(() => {
  vi.unstubAllGlobals();
  resetRequestKeys();
});

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

describe('requestKeyFor', () => {
  it('gives one intent the same key until it is released, and different intents different keys', () => {
    const first = requestKeyFor('extra:t1:2026-09-16:u1');
    expect(requestKeyFor('extra:t1:2026-09-16:u1')).toBe(first);
    expect(requestKeyFor('extra:t2:2026-09-16:u1')).not.toBe(first);

    releaseRequestKey('extra:t1:2026-09-16:u1');
    expect(requestKeyFor('extra:t1:2026-09-16:u1')).not.toBe(first);
  });

  it('keeps a key for ten minutes, then a deliberate repeat of the same intent gets a new one', () => {
    expect(REQUEST_KEY_TTL_MS).toBe(10 * 60 * 1000);
    const start = 1_000_000;
    const first = requestKeyFor('redeem:a', start);
    expect(requestKeyFor('redeem:a', start + REQUEST_KEY_TTL_MS - 1)).toBe(first);
    const later = requestKeyFor('redeem:a', start + REQUEST_KEY_TTL_MS);
    expect(later).not.toBe(first);
    // The new key starts its own ten minutes.
    expect(requestKeyFor('redeem:a', start + REQUEST_KEY_TTL_MS + 5)).toBe(later);
    expect(requestKeyFor('redeem:b', start + REQUEST_KEY_TTL_MS)).not.toBe(later);
  });

  it('uses the clock when no time is given', () => {
    vi.useFakeTimers();
    try {
      vi.setSystemTime(new Date('2026-09-16T08:00:00Z'));
      const first = requestKeyFor('redeem:clock');
      vi.setSystemTime(new Date('2026-09-16T08:09:59Z'));
      expect(requestKeyFor('redeem:clock')).toBe(first);
      vi.setSystemTime(new Date('2026-09-16T08:10:01Z'));
      expect(requestKeyFor('redeem:clock')).not.toBe(first);
    } finally {
      vi.useRealTimers();
    }
  });

  it('is a valid key the server accepts', () => {
    expect(requestKeySchema.safeParse(requestKeyFor('x')).success).toBe(true);
  });
});

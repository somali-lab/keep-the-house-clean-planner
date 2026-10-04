import { describe, expect, it } from 'vitest';
import { isCacheableApiRequest, profilePartitionedKey } from './apiCache.ts';

const rule = (path: string, method = 'GET') => isCacheableApiRequest({ url: new URL(`http://app.test${path}`), request: { method } });

describe('isCacheableApiRequest', () => {
  it.each([
    '/api/v2/occurrences?from=2026-09-16&to=2026-09-16',
    '/api/v2/users',
    '/api/v2/tasks',
    '/api/v2/settings',
    '/api/v2/meta/limits',
    '/api/v2/calendar',
    '/api/v2/points/balances',
    '/api/v2/badges/abc/image',
  ])('keeps %s for offline reading', (path) => {
    expect(rule(path)).toBe(true);
  });

  it.each([
    '/api/v2/export/pdf/schedule',
    '/api/v2/export/json',
    '/api/v2/audit',
    '/api/v2/audit?limit=50',
    '/api/v2/health',
    '/api/v2/ai/prompt-info',
    '/api/users',
    '/api/export/json',
    '/rooms',
    '/api/v2',
  ])('never keeps %s', (path) => {
    expect(rule(path)).toBe(false);
  });

  it('never keeps a write', () => {
    expect(rule('/api/v2/occurrences/abc/complete', 'POST')).toBe(false);
    expect(rule('/api/v2/users/abc', 'PATCH')).toBe(false);
  });

  it('does not mistake a path that merely starts like an excluded one', () => {
    expect(rule('/api/v2/auditors')).toBe(true);
    expect(rule('/api/v2/healthy')).toBe(true);
  });
});

describe('profilePartitionedKey', () => {
  const key = (profile: string | null) =>
    profilePartitionedKey({
      request: new Request('http://app.test/api/v2/occurrences?from=2026-09-16', profile ? { headers: { 'X-Profile-Id': profile } } : {}),
    });

  it('gives two profiles two entries for the same URL', () => {
    expect(key('anna')).not.toBe(key('bram'));
  });

  it('gives the same profile the same entry, and keeps the query', () => {
    expect(key('anna')).toBe(key('anna'));
    expect(key('anna')).toContain('from=2026-09-16');
  });

  it('keeps a request without a profile apart from every profile', () => {
    expect(key(null)).not.toBe(key('anna'));
    expect(key(null)).toContain('x-profile=none');
  });
});

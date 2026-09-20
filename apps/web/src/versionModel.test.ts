import { describe, expect, it } from 'vitest';
import { buildAppVersion, localBuildTimestamp } from './versionModel.ts';

describe('build version', () => {
  const builtAt = new Date('2026-09-20T09:32:45.678Z');

  it('adds a local marker and UTC timestamp to source builds', () => {
    expect(localBuildTimestamp(builtAt)).toBe('20260920-093245Z');
    expect(buildAppVersion('1.3.0\n', false, builtAt)).toBe('1.3.0-local-20260920-093245Z');
  });

  it('keeps the exact release version for official images', () => {
    expect(buildAppVersion('1.3.0\n', true, builtAt)).toBe('1.3.0');
  });
});

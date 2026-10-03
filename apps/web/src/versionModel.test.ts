import { describe, expect, it } from 'vitest';
import { buildAppVersion, buildReleaseDate, localBuildTimestamp, sourceRef } from './versionModel.ts';

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

describe('release metadata', () => {
  it('keeps the release moment of an official image', () => {
    expect(buildReleaseDate('2026-09-28T10:15:00+02:00\n', true)).toBe('2026-09-28T08:15:00.000Z');
  });

  it('never gives a local build a release moment', () => {
    expect(buildReleaseDate('2026-09-28T10:15:00+02:00', false)).toBeNull();
    expect(buildReleaseDate(undefined, true)).toBeNull();
    expect(buildReleaseDate('', true)).toBeNull();
    expect(buildReleaseDate('not a date', true)).toBeNull();
  });

  it('links an official image to its release tag and a local build to main', () => {
    expect(sourceRef('1.6.2\n', true)).toBe('v1.6.2');
    expect(sourceRef('1.6.2\n', false)).toBe('main');
  });
});

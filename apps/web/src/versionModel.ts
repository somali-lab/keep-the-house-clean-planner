/** Stable UTC identifier used to distinguish source builds from published images. */
export function localBuildTimestamp(now: Date): string {
  return now.toISOString().replace(/[-:]/g, '').replace('T', '-').replace(/\.\d{3}Z$/, 'Z');
}

export function buildAppVersion(releaseVersion: string, officialBuild: boolean, now = new Date()): string {
  const version = releaseVersion.trim();
  return officialBuild ? version : `${version}-local-${localBuildTimestamp(now)}`;
}

/**
 * The moment of the release this build was made from, or null. Only official images carry it;
 * a local build is not a release, so it never gets a release moment of its own.
 */
export function buildReleaseDate(value: string | undefined, officialBuild: boolean): string | null {
  if (!officialBuild || !value) return null;
  const moment = new Date(value.trim());
  return Number.isNaN(moment.getTime()) ? null : moment.toISOString();
}

/** Git ref whose LICENSE and CHANGELOG match the running build: the release tag, or main for a local build. */
export function sourceRef(releaseVersion: string, officialBuild: boolean): string {
  return officialBuild ? `v${releaseVersion.trim()}` : 'main';
}

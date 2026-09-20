/** Stable UTC identifier used to distinguish source builds from published images. */
export function localBuildTimestamp(now: Date): string {
  return now.toISOString().replace(/[-:]/g, '').replace('T', '-').replace(/\.\d{3}Z$/, 'Z');
}

export function buildAppVersion(releaseVersion: string, officialBuild: boolean, now = new Date()): string {
  const version = releaseVersion.trim();
  return officialBuild ? version : `${version}-local-${localBuildTimestamp(now)}`;
}

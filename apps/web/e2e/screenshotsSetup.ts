import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import globalSetup from './globalSetup.ts';

/**
 * Builds the web app as a release build first, so the version badge in the
 * published screenshots carries no local build timestamp, then starts the
 * MongoDB the capture run needs.
 */
export default async function screenshotsSetup() {
  const build = spawnSync('npm', ['run', 'build'], {
    cwd: resolve(import.meta.dirname, '..'),
    env: { ...process.env, APP_OFFICIAL_BUILD: 'true' },
    stdio: 'inherit',
    shell: true,
  });
  if (build.status !== 0) throw new Error(`web build failed with ${build.status}`);
  return globalSetup();
}

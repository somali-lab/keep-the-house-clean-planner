import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { startThrowawayMongo } from './mongo.ts';

const ROOT = resolve(import.meta.dirname, '../../..');
/** Where the .NET host is published for the run (ignored by git). */
export const HOST_DIR = resolve(import.meta.dirname, '../.e2e-host');

/** Publishes the .NET host once per run; a given E2E_HOST_DIR (an earlier publish) is used as it is. */
function publishHost() {
  if (process.env.E2E_HOST_DIR) {
    process.env.E2E_HOST_DLL = resolve(process.env.E2E_HOST_DIR, 'Huishoudplanner.Host.dll');
    return;
  }
  const result = spawnSync(
    'dotnet',
    [
      'publish', 'apps/api/src/Huishoudplanner.Host/Huishoudplanner.Host.csproj',
      '--configuration', 'Release', '--output', HOST_DIR, '/p:UseAppHost=false', '--nologo', '--verbosity', 'quiet',
    ],
    { cwd: ROOT, stdio: 'inherit' },
  );
  if (result.error) throw new Error(`dotnet is not available (${result.error.message}); the e2e suite runs the .NET host`);
  if (result.status !== 0) throw new Error(`dotnet publish failed with ${result.status}`);
  process.env.E2E_HOST_DLL = resolve(HOST_DIR, 'Huishoudplanner.Host.dll');
}

/**
 * One throwaway MongoDB replica set for the whole run (or E2E_MONGO_URL, which must be a replica set); each test
 * uses its own database on it and its own .NET host process on its own port.
 */
export default async function globalSetup() {
  if (!existsSync(resolve(import.meta.dirname, '../dist/index.html'))) {
    throw new Error('apps/web/dist is missing. Run "npm run test:e2e", which builds first.');
  }
  publishHost();
  if (process.env.E2E_MONGO_URL) return;
  const mongo = await startThrowawayMongo();
  process.env.E2E_MONGO_URL = mongo.url;
  return async () => {
    mongo.stop();
  };
}

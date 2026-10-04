import { realpathSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

/**
 * True when the module at `metaUrl` is the script node was started with. Works on every Node 24 (`import.meta.main` only exists
 * from 24.2, and on an older one it would silently make a script do nothing). Fails loudly on a Node older than 24.
 */
export function isEntry(metaUrl: string): boolean {
  const major = Number(process.versions.node.split('.')[0]);
  if (major < 24) {
    console.error(`Node.js 24 or newer is needed on this host (found ${process.versions.node}).`);
    process.exit(2);
  }
  const entry = process.argv[1];
  if (!entry) return false;
  try {
    return realpathSync(entry) === realpathSync(fileURLToPath(metaUrl));
  } catch {
    return false;
  }
}

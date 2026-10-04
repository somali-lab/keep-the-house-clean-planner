import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { createServer } from 'node:net';

const IMAGE = 'mongo:8';
const REPLICA_SET = 'rs0';
const READY_TIMEOUT_MS = 120_000;

export function freePort(): Promise<number> {
  return new Promise((resolvePort, reject) => {
    const server = createServer();
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      const port = typeof address === 'object' && address ? address.port : 0;
      server.close(() => resolvePort(port));
    });
  });
}

function docker(args: string[], { allowFailure = false } = {}) {
  const result = spawnSync('docker', args, { encoding: 'utf8' });
  if (result.error) throw new Error(`docker is not available (${result.error.message}); the e2e suite needs Docker for MongoDB`);
  if (result.status !== 0 && !allowFailure) throw new Error(`docker ${args.join(' ')} failed:\n${result.stderr}`);
  return result;
}

export interface ThrowawayMongo {
  /** Connection string of the single-node replica set, without a database name. */
  url: string;
  stop(): void;
}

/**
 * Starts a throwaway single-node MongoDB replica set (the .NET application needs transactions, ADR-0021) in its own
 * container on a random loopback port, and never touches any other container. The member host is the published
 * address itself (the container listens on the same port it publishes), so the driver can reach it from the host.
 */
export async function startThrowawayMongo(): Promise<ThrowawayMongo> {
  const port = await freePort();
  const name = `kthc-e2e-mongo-${randomBytes(4).toString('hex')}`;
  docker([
    'run', '--detach', '--rm', '--name', name, '--label', 'kthc-e2e=true',
    '--publish', `127.0.0.1:${port}:${port}`,
    IMAGE, '--replSet', REPLICA_SET, '--port', String(port), '--bind_ip_all',
  ]);
  const stop = () => {
    docker(['rm', '--force', name], { allowFailure: true });
  };

  try {
    const initiate = `try { rs.status() } catch (e) { rs.initiate({ _id: '${REPLICA_SET}', members: [{ _id: 0, host: '127.0.0.1:${port}' }] }) }; if (!db.hello().isWritablePrimary) quit(1)`;
    const deadline = Date.now() + READY_TIMEOUT_MS;
    for (;;) {
      const probe = docker(['exec', name, 'mongosh', '--port', String(port), '--quiet', '--eval', initiate], { allowFailure: true });
      if (probe.status === 0) break;
      if (Date.now() > deadline) throw new Error(`MongoDB did not become primary within ${READY_TIMEOUT_MS} ms:\n${probe.stderr}${probe.stdout}`);
      await new Promise((resolveWait) => setTimeout(resolveWait, 1000));
    }
  } catch (error) {
    stop();
    throw error;
  }
  return { url: `mongodb://127.0.0.1:${port}/?replicaSet=${REPLICA_SET}`, stop };
}

import { spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';
import { createServer } from 'node:net';
import { resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '../../..');
const STARTUP_TIMEOUT_MS = 30_000;

export const SEED_USERS = [
  { name: 'Anna', color: '#2563eb' },
  { name: 'Bram', color: '#db2777' },
];

export interface ApiUser {
  _id: string;
  name: string;
}

export interface AppServer {
  baseURL: string;
  /** The server's APP_FAKE_NOW. */
  now: string;
  /** JSON request; `as` sends X-Profile-Id. Throws on a non-2xx answer. */
  api<T = unknown>(method: string, path: string, options?: { body?: unknown; as?: ApiUser }): Promise<T>;
  user(name: string): Promise<ApiUser>;
  /** Restarts on the same database with another fake now (e.g. to let time pass). */
  restart(now: string): Promise<void>;
  stop(): Promise<void>;
}

function freePort(): Promise<number> {
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

/** Starts apps/server (node src/main.ts) with the built web app, a fixed clock and no scheduler. */
export async function startServer(options: { now: string; database: string }): Promise<AppServer> {
  const mongo = process.env.E2E_MONGO_URL;
  if (!mongo) throw new Error('E2E_MONGO_URL is not set; globalSetup did not run');
  const mongoUrl = new URL(mongo);
  mongoUrl.pathname = `/${options.database}`;
  const port = await freePort();
  const baseURL = `http://127.0.0.1:${port}`;

  let child: ChildProcess | null = null;
  let output = '';

  // Playwright's own loader must not leak into the server process.
  const { NODE_OPTIONS: _nodeOptions, ...inheritedEnv } = process.env;

  const launch = async (now: string) => {
    output = '';
    const proc = spawn(process.execPath, ['src/main.ts'], {
      cwd: resolve(ROOT, 'apps/server'),
      env: {
        ...inheritedEnv,
        NODE_ENV: 'test',
        PORT: String(port),
        MONGO_URL: mongoUrl.toString(),
        APP_FAKE_NOW: now,
        DISABLE_SCHEDULER: 'true',
        LOG_LEVEL: 'warn',
        TZ_APP: 'Europe/Amsterdam',
        SEED_USERS: JSON.stringify(SEED_USERS),
        WEB_DIST_DIR: resolve(ROOT, 'apps/web/dist'),
        NOTIFY_TYPE: 'none',
        AI_API_KEY: '',
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    proc.stdout?.on('data', (chunk: Buffer) => (output += chunk.toString()));
    proc.stderr?.on('data', (chunk: Buffer) => (output += chunk.toString()));
    child = proc;

    const deadline = Date.now() + STARTUP_TIMEOUT_MS;
    while (Date.now() < deadline) {
      if (proc.exitCode !== null) throw new Error(`server exited with ${proc.exitCode}:\n${output}`);
      try {
        if ((await fetch(`${baseURL}/api/health`)).ok) return;
      } catch {
        // not listening yet
      }
      await new Promise((r) => setTimeout(r, 100));
    }
    throw new Error(`server did not become healthy within ${STARTUP_TIMEOUT_MS} ms:\n${output}`);
  };

  const halt = async () => {
    const proc = child;
    child = null;
    if (!proc || proc.exitCode !== null) return;
    const exited = once(proc, 'exit');
    proc.kill();
    await exited;
  };

  const app: AppServer = {
    baseURL,
    now: options.now,
    async api<T>(method: string, path: string, { body, as }: { body?: unknown; as?: ApiUser } = {}) {
      const res = await fetch(`${baseURL}${path}`, {
        method,
        headers: {
          ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
          ...(as ? { 'X-Profile-Id': as._id } : {}),
        },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      const text = await res.text();
      if (!res.ok) throw new Error(`${method} ${path} answered ${res.status}: ${text}`);
      return (text ? JSON.parse(text) : undefined) as T;
    },
    async user(name) {
      const user = (await app.api<ApiUser[]>('GET', '/api/users')).find((u) => u.name === name);
      if (!user) throw new Error(`no user named ${name}`);
      return user;
    },
    async restart(now) {
      await halt();
      app.now = now;
      await launch(now);
    },
    stop: halt,
  };

  await launch(options.now);
  return app;
}

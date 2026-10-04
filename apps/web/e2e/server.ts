import { spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';
import { resolve } from 'node:path';
import { freePort } from './mongo.ts';

const ROOT = resolve(import.meta.dirname, '../../..');
const STARTUP_TIMEOUT_MS = 60_000;

export const SEED_USERS = [
  { name: 'Anna', color: '#2563eb' },
  { name: 'Bram', color: '#db2777' },
];

/**
 * The rooms of a household as the Node server seeded them. The .NET host seeds users, settings and the default plan
 * but no rooms yet (docs/BLOCKERS.md), so the harness creates them through the API, as the first profile.
 */
export const SEED_ROOMS = [
  { name: 'Keuken', virtual: false },
  { name: 'Badkamer', virtual: false },
  { name: 'Toilet', virtual: false },
  { name: 'Woonkamer', virtual: false },
  { name: 'Slaapkamer', virtual: false },
  { name: 'Hal', virtual: false },
  { name: 'Hele huis', virtual: true },
];

export interface ApiUser {
  id: string;
  name: string;
}

export interface ApiOptions {
  body?: unknown;
  /** Sends X-Profile-Id. */
  as?: ApiUser;
  /** Sends If-Match (the entity writes of /api/v2 need it). */
  ifMatch?: number | string;
}

export interface ApiAnswer<T> {
  status: number;
  data: T;
  etag: string | null;
}

export interface AppServer {
  baseURL: string;
  /** The server's APP_FAKE_NOW. */
  now: string;
  /** JSON request to /api/v2; throws on a non-2xx answer. */
  api<T = unknown>(method: string, path: string, options?: ApiOptions): Promise<T>;
  /** Like `api`, with the status and the ETag of the answer. */
  request<T = unknown>(method: string, path: string, options?: ApiOptions): Promise<ApiAnswer<T>>;
  /** Every item of a paged list (follows `nextCursor`). */
  list<T = unknown>(path: string, options?: ApiOptions): Promise<T[]>;
  /** Reads the entity, then PATCHes it with the `If-Match` of what was read (the version a form would carry). */
  edit<T = unknown>(path: string, body: unknown, options: { as: ApiUser; method?: 'PATCH' | 'PUT' }): Promise<T>;
  user(name: string): Promise<ApiUser>;
  /** Restarts on the same database with another fake now (e.g. to let time pass). */
  restart(now: string): Promise<void>;
  stop(): Promise<void>;
}

/**
 * Starts the .NET host (published by globalSetup) with the built web app, a fixed clock and no scheduler,
 * on a fresh database of the run's throwaway MongoDB replica set.
 */
export async function startServer(options: { now: string; database: string }): Promise<AppServer> {
  const mongo = process.env.E2E_MONGO_URL;
  const hostDll = process.env.E2E_HOST_DLL;
  if (!mongo || !hostDll) throw new Error('E2E_MONGO_URL or E2E_HOST_DLL is not set; globalSetup did not run');
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
    const proc = spawn('dotnet', [hostDll], {
      cwd: resolve(hostDll, '..'),
      env: {
        ...inheritedEnv,
        ASPNETCORE_ENVIRONMENT: 'test',
        ASPNETCORE_URLS: baseURL,
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
        DOTNET_NOLOGO: '1',
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
        if ((await fetch(`${baseURL}/api/v2/health`)).ok) return;
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

  const request = async <T>(method: string, path: string, { body, as, ifMatch }: ApiOptions = {}): Promise<ApiAnswer<T>> => {
    const res = await fetch(`${baseURL}${path}`, {
      method,
      headers: {
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        ...(as ? { 'X-Profile-Id': as.id } : {}),
        ...(ifMatch === undefined ? {} : { 'If-Match': `"${ifMatch}"` }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    if (!res.ok) throw new Error(`${method} ${path} answered ${res.status}: ${text}`);
    return { status: res.status, data: (text ? JSON.parse(text) : undefined) as T, etag: res.headers.get('ETag') };
  };

  const app: AppServer = {
    baseURL,
    now: options.now,
    request,
    async api<T>(method: string, path: string, apiOptions: ApiOptions = {}) {
      return (await request<T>(method, path, apiOptions)).data;
    },
    async list<T>(path: string, apiOptions: ApiOptions = {}) {
      const items: T[] = [];
      let cursor: string | null = null;
      do {
        const separator = path.includes('?') ? '&' : '?';
        const url: string = `${path}${separator}limit=200${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`;
        const page: { items: T[]; nextCursor: string | null } = await app.api(
          'GET',
          url,
          apiOptions,
        );
        items.push(...page.items);
        cursor = page.nextCursor;
      } while (cursor);
      return items;
    },
    async edit<T>(path: string, body: unknown, { as, method = 'PATCH' }: { as: ApiUser; method?: 'PATCH' | 'PUT' }) {
      const current = await request<{ version: number }>('GET', path, { as });
      return (await request<T>(method, path, { as, body, ifMatch: current.data.version })).data;
    },
    async user(name) {
      const user = (await app.list<ApiUser>('/api/v2/users')).find((u) => u.name === name);
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
  if ((await app.list('/api/v2/rooms')).length === 0) {
    const admin = await app.user(SEED_USERS[0]!.name);
    for (const [index, room] of SEED_ROOMS.entries()) {
      await app.api('POST', '/api/v2/rooms', { as: admin, body: { ...room, sortOrder: (index + 1) * 10 } });
    }
  }
  return app;
}

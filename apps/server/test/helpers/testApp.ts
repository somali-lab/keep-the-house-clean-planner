import { randomUUID } from 'node:crypto';
import type { FastifyInstance } from 'fastify';
import { MongoClient, type Db } from 'mongodb';
import { inject } from 'vitest';
import { buildApp } from '../../src/app.ts';
import { systemContext, type AuditContext } from '../../src/audit/context.ts';
import { fixedClock, type FixedClock } from '../../src/clock.ts';
import { loadConfig, type AppConfig } from '../../src/config.ts';
import { ensureIndexes } from '../../src/data/db.ts';
import type { AiProvider } from '../../src/domain/ai/provider.ts';
import { seed } from '../../src/domain/seed.ts';

export const DEFAULT_TEST_NOW = '2026-09-16T08:00:00.000Z'; // Wednesday 10:00 Amsterdam

export interface TestApp {
  app: FastifyInstance;
  db: Db;
  dbName: string;
  /** Client with command monitoring enabled (see helpers/audit.ts). */
  client: MongoClient;
  clock: FixedClock;
  config: AppConfig;
  systemCtx(): AuditContext;
  close(): Promise<void>;
}

export interface CreateTestAppOptions {
  now?: string;
  env?: Record<string, string>;
  configure?: (app: FastifyInstance) => void | Promise<void>;
  /** Run the startup seed (default true). */
  seed?: boolean;
  /** Use this provider regardless of settings (tests never call real AI APIs). */
  aiProvider?: AiProvider;
}

function withDatabase(uri: string, dbName: string): string {
  const url = new URL(uri);
  url.pathname = `/${dbName}`;
  return url.toString();
}

/** Fresh database per call, fixed clock, scheduler disabled, logging off. */
export async function createTestApp(options: CreateTestAppOptions = {}): Promise<TestApp> {
  const dbName = `test_${randomUUID().replaceAll('-', '')}`;
  const mongoUrl = withDatabase(inject('mongoUri'), dbName);
  const config = loadConfig({
    NODE_ENV: 'test',
    MONGO_URL: mongoUrl,
    DISABLE_SCHEDULER: 'true',
    LOG_LEVEL: 'silent',
    ...options.env,
  });
  const client = new MongoClient(mongoUrl, { monitorCommands: true });
  await client.connect();
  const db = client.db(dbName);
  await ensureIndexes(db);
  const clock = fixedClock(options.now ?? DEFAULT_TEST_NOW);
  const aiProvider = options.aiProvider;
  const app = await buildApp({
    db,
    config,
    clock,
    logger: false,
    configure: options.configure,
    ...(aiProvider ? { aiProviderFactory: () => aiProvider } : {}),
  });
  await app.ready();
  if (options.seed !== false) await seed(systemContext({ db, clock }, app.log), config);

  return {
    app,
    db,
    dbName,
    client,
    clock,
    config,
    systemCtx: () => systemContext({ db, clock }, app.log),
    async close() {
      await app.close();
      await db.dropDatabase();
      await client.close();
    },
  };
}

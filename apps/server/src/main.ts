import { buildApp } from './app.ts';
import { systemContext } from './audit/context.ts';
import { fixedClock, systemClock } from './clock.ts';
import { ConfigError, loadConfig } from './config.ts';
import { connectMongo, ensureIndexes } from './data/db.ts';
import { backfillOccurrenceRoomSnapshots } from './data/occurrences.ts';
import { seed } from './domain/seed.ts';
import { startScheduler } from './jobs/nightly.ts';

async function main(): Promise<void> {
  let config;
  try {
    config = loadConfig();
  } catch (err) {
    if (err instanceof ConfigError) {
      console.error(JSON.stringify({ level: 'fatal', msg: err.message }));
      process.exit(1);
    }
    throw err;
  }

  const { client, db } = await connectMongo(config.mongoUrl);
  await ensureIndexes(db);
  await backfillOccurrenceRoomSnapshots(db);

  const clock = config.fakeNow ? fixedClock(config.fakeNow) : systemClock;
  const app = await buildApp({ db, config, clock });
  await seed(systemContext({ db, clock }, app.log), config);

  let scheduler: ReturnType<typeof startScheduler> = null;
  let shuttingDown = false;
  const shutdown = async (signal: string) => {
    if (shuttingDown) return;
    shuttingDown = true;
    app.log.info({ signal }, 'shutting down');
    try {
      scheduler?.stop();
      await app.close();
      await client.close();
      process.exit(0);
    } catch (err) {
      app.log.error({ err }, 'error during shutdown');
      process.exit(1);
    }
  };
  process.on('SIGTERM', () => void shutdown('SIGTERM'));
  process.on('SIGINT', () => void shutdown('SIGINT'));

  await app.listen({ port: config.port, host: '0.0.0.0' });
  scheduler = startScheduler(app);
}

main().catch((err: unknown) => {
  console.error(JSON.stringify({ level: 'fatal', msg: 'startup failed', err: String(err) }));
  process.exit(1);
});

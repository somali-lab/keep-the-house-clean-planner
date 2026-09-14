import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { MongoMemoryServer } from 'mongodb-memory-server';

/** One Mongo for the whole run (or MONGO_TEST_URL); each test uses its own database on it. */
export default async function globalSetup() {
  if (!existsSync(resolve(import.meta.dirname, '../dist/index.html'))) {
    throw new Error('apps/web/dist is missing. Run "npm run test:e2e", which builds first.');
  }
  if (process.env.MONGO_TEST_URL) {
    process.env.E2E_MONGO_URL = process.env.MONGO_TEST_URL;
    return;
  }
  const mongo = await MongoMemoryServer.create();
  process.env.E2E_MONGO_URL = mongo.getUri();
  return async () => {
    await mongo.stop();
  };
}

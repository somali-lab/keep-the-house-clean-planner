import { MongoMemoryServer } from 'mongodb-memory-server';
import type { TestProject } from 'vitest/node';

declare module 'vitest' {
  export interface ProvidedContext {
    mongoUri: string;
  }
}

/** One Mongo for the whole run; each test file gets its own database (see createTestApp). */
export default async function setup(project: TestProject) {
  const external = process.env.MONGO_TEST_URL;
  if (external) {
    project.provide('mongoUri', external);
    return;
  }
  const server = await MongoMemoryServer.create();
  project.provide('mongoUri', server.getUri());
  return async () => {
    await server.stop();
  };
}

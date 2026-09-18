import type { FastifyPluginAsync } from 'fastify';
import { pingMongo } from '../data/db.ts';

export const healthRoutes: FastifyPluginAsync = async (app) => {
  app.get('/health', { logLevel: 'silent' }, async (_request, reply) => {
    const mongoOk = await pingMongo(app.deps.db);
    if (!mongoOk) {
      app.log.warn('health check failed: MongoDB is unavailable');
      return reply.status(503).send({ status: 'error', mongo: 'error' });
    }
    return { status: 'ok', mongo: 'ok' };
  });
};

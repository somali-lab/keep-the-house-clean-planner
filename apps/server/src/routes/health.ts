import type { FastifyPluginAsync } from 'fastify';
import { pingMongo } from '../data/db.ts';

export const healthRoutes: FastifyPluginAsync = async (app) => {
  app.get('/health', async (_request, reply) => {
    const mongoOk = await pingMongo(app.deps.db);
    if (!mongoOk) return reply.status(503).send({ status: 'error', mongo: 'error' });
    return { status: 'ok', mongo: 'ok' };
  });
};

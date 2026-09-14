import type { FastifyPluginAsync } from 'fastify';
import { listCycles } from '../data/cycles.ts';
import { toApi } from '../http/serialize.ts';

/** Read-only list of generated cycles; the export dialog uses it to know which weeks exist. */
export const cycleRoutes: FastifyPluginAsync = async (app) => {
  app.get('/cycles', async () => toApi(await listCycles(app.deps.db)));
};

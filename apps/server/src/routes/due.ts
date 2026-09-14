import type { FastifyPluginAsync } from 'fastify';
import { computeDueList } from '../domain/due.ts';
import { toApi } from '../http/serialize.ts';

/** Ranked "achterstand" list, independent of the grid. Includes tasks that are fine (state 'ok'). */
export const dueRoutes: FastifyPluginAsync = async (app) => {
  app.get('/due', async () => {
    const { items } = await computeDueList(app.deps.db, app.deps.clock.now());
    return toApi(items);
  });
};

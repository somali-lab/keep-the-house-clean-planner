import { completionQuerySchema, statsCyclesQuerySchema } from '@huishoudplanner/shared';
import type { FastifyPluginAsync } from 'fastify';
import { completionStats, deviationStats, intervalStats, resetStatistics, workloadStats } from '../domain/stats.ts';
import { parseOrThrow } from '../http/errors.ts';
import { auditContext, requireAdmin } from '../identity/index.ts';

export const statsRoutes: FastifyPluginAsync = async (app) => {
  app.get('/stats/workload', async (request) => {
    const { cycles, weeks } = parseOrThrow(statsCyclesQuerySchema, request.query);
    return workloadStats(app.deps.db, app.deps.clock.now(), cycles, weeks);
  });

  app.get('/stats/completion', async (request) => {
    const { cycles, weeks, groupBy } = parseOrThrow(completionQuerySchema, request.query);
    return completionStats(app.deps.db, app.deps.clock.now(), cycles, groupBy, weeks);
  });

  app.get('/stats/intervals', async (request) => {
    const { cycles, weeks } = parseOrThrow(statsCyclesQuerySchema, request.query);
    return intervalStats(app.deps.db, app.deps.clock.now(), cycles, weeks);
  });

  app.get('/stats/deviations', async (request) => {
    const { cycles, weeks } = parseOrThrow(statsCyclesQuerySchema, request.query);
    return deviationStats(app.deps.db, app.deps.clock.now(), cycles, weeks);
  });

  app.delete('/stats', { preHandler: requireAdmin }, async (request) => resetStatistics(auditContext(request)));
};

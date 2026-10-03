import { pointsBalancesQuerySchema, pointsEntriesQuerySchema } from '@huishoudplanner/shared';
import type { FastifyPluginAsync } from 'fastify';
import { pointEntriesOfPerson, pointsBalances, reconcilePoints } from '../domain/points.ts';
import { parseOrThrow } from '../http/errors.ts';
import { auditContext, requireAdmin } from '../identity/index.ts';

/** Reads need no profile, as everywhere else (ADR-0011). */
export const pointsRoutes: FastifyPluginAsync = async (app) => {
  app.get('/points/balances', async (request) => {
    return pointsBalances(app.deps.db, parseOrThrow(pointsBalancesQuerySchema, request.query));
  });

  app.get('/points/entries', async (request) => {
    return pointEntriesOfPerson(app.deps.db, parseOrThrow(pointsEntriesQuerySchema, request.query));
  });

  /** Reconciles the ledger with the occurrences now; one summary audit entry when anything changed. */
  app.post('/points/recompute', { preHandler: requireAdmin }, async (request) => {
    return reconcilePoints(auditContext(request), 'admin');
  });
};

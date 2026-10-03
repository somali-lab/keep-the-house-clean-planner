import { createRedemptionInputSchema, pointsBalancesQuerySchema, pointsEntriesQuerySchema } from '@huishoudplanner/shared';
import type { FastifyPluginAsync } from 'fastify';
import { ObjectId } from 'mongodb';
import { getSettings } from '../data/settings.ts';
import { pointEntriesOfPerson, pointsBalances, reconcilePoints, toPointEntryView } from '../domain/points.ts';
import { bookRedemption, undoRedemption } from '../domain/redemptions.ts';
import { HttpError, notFound, parseOrThrow } from '../http/errors.ts';
import { parseIdParam } from '../http/params.ts';
import { auditContext, requireActor, requireAdmin } from '../identity/index.ts';

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

  /**
   * Books a redemption (ADR-0013): a person gives up points for themselves, an administrator for anyone.
   * 201 for a new booking, 200 when a repeated requestId replays the stored one, 409 when the balance is too low.
   */
  app.post('/points/redemptions', { preHandler: requireActor }, async (request, reply) => {
    const input = parseOrThrow(createRedemptionInputSchema, request.body);
    const ctx = auditContext(request);
    const personId = input.personId ? new ObjectId(input.personId) : ctx.actorId;
    if (!personId.equals(ctx.actorId) && request.actor?.role !== 'admin') {
      throw new HttpError(403, 'permission_denied', 'Only an administrator can book a redemption for someone else');
    }
    const result = await bookRedemption(ctx, {
      personId,
      points: input.points,
      note: input.note ? input.note : null,
      ...(input.requestId === undefined ? {} : { requestId: input.requestId }),
    });
    const settings = await getSettings(app.deps.db);
    if (!settings) throw notFound('settings');
    return reply.status(result.created ? 201 : 200).send(toPointEntryView(result.doc, settings.timezone));
  });

  /** Takes a redemption back: the person it belongs to on the day it was booked, an administrator at any time. */
  app.delete('/points/redemptions/:id', { preHandler: requireActor }, async (request) => {
    const id = parseIdParam(request.params);
    await undoRedemption(auditContext(request), id, request.actor!.role);
    return { deleted: true };
  });
};

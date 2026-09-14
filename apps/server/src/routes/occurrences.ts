import {
  addDays,
  createOccurrenceInputSchema,
  fromDayKey,
  listOccurrencesQuerySchema,
  patchOccurrenceInputSchema,
  today,
} from '@huishoudplanner/shared';
import type { FastifyPluginAsync } from 'fastify';
import { ObjectId, type Filter } from 'mongodb';
import { findOccurrences, type OccurrenceDoc } from '../data/occurrences.ts';
import { getSettings } from '../data/settings.ts';
import {
  assignOccurrence,
  claimOccurrence,
  completeOccurrence,
  createAdhocOccurrence,
  rescheduleOccurrence,
  skipOccurrence,
  toOccurrenceView,
  uncompleteOccurrence,
  type ChangeResult,
} from '../domain/occurrences.ts';
import { HttpError, notFound, parseOrThrow } from '../http/errors.ts';
import { parseIdParam } from '../http/params.ts';
import { auditContext, requireActor } from '../identity/index.ts';

export const occurrenceRoutes: FastifyPluginAsync = async (app) => {
  async function viewContext() {
    const settings = await getSettings(app.deps.db);
    if (!settings) throw notFound('settings');
    return { timezone: settings.timezone, todayKey: today(settings.timezone, app.deps.clock.now()) };
  }

  async function view(doc: OccurrenceDoc) {
    const { timezone, todayKey } = await viewContext();
    return toOccurrenceView(doc, todayKey, timezone);
  }

  /** Reschedule and assign responses carry non-blocking warnings. */
  async function viewWithWarnings(result: ChangeResult) {
    return { ...(await view(result.doc)), warnings: result.warnings };
  }

  app.get('/occurrences', async (request) => {
    const query = parseOrThrow(listOccurrencesQuerySchema, request.query);
    if (query.from > query.to) {
      throw new HttpError(400, 'validation_error', 'from must not be after to', [
        { field: 'from', message: 'from_after_to' },
      ]);
    }
    const { timezone, todayKey } = await viewContext();
    const filter: Filter<OccurrenceDoc> = {
      date: { $gte: fromDayKey(query.from, timezone), $lt: fromDayKey(addDays(query.to, 1), timezone) },
      ...(query.assigneeId ? { assigneeId: new ObjectId(query.assigneeId) } : {}),
      ...(query.status ? { status: query.status } : {}),
    };
    const docs = await findOccurrences(app.deps.db, filter);
    return docs.map((doc) => toOccurrenceView(doc, todayKey, timezone));
  });

  app.post('/occurrences', { preHandler: requireActor }, async (request, reply) => {
    const input = parseOrThrow(createOccurrenceInputSchema, request.body);
    const doc = await createAdhocOccurrence(auditContext(request), {
      taskId: new ObjectId(input.taskId),
      date: input.date,
      ...(input.assigneeId === undefined
        ? {}
        : { assigneeId: input.assigneeId === null ? null : new ObjectId(input.assigneeId) }),
    });
    return reply.status(201).send(await view(doc));
  });

  app.patch('/occurrences/:id', { preHandler: requireActor }, async (request) => {
    const id = parseIdParam(request.params);
    const input = parseOrThrow(patchOccurrenceInputSchema, request.body);
    const ctx = auditContext(request);
    switch (input.action) {
      case 'complete':
        return view(await completeOccurrence(ctx, id, input.completedBy ? new ObjectId(input.completedBy) : undefined));
      case 'uncomplete':
        return view(await uncompleteOccurrence(ctx, id));
      case 'skip':
        return view(await skipOccurrence(ctx, id, input.reason));
      case 'reschedule':
        return viewWithWarnings(await rescheduleOccurrence(ctx, id, input.date));
      case 'assign':
        return viewWithWarnings(
          await assignOccurrence(ctx, id, input.assigneeId === null ? null : new ObjectId(input.assigneeId)),
        );
    }
  });

  app.post('/occurrences/:id/claim', { preHandler: requireActor }, async (request) => {
    const id = parseIdParam(request.params);
    return view(await claimOccurrence(auditContext(request), id));
  });
};

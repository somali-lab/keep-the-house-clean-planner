import {
  createCyclePlanInputSchema,
  putSlotsInputSchema,
  updateCyclePlanInputSchema,
} from '@huishoudplanner/shared';
import type { FastifyPluginAsync } from 'fastify';
import { ObjectId } from 'mongodb';
import {
  createPlan,
  deletePlan,
  EMPTY_WEEK_THEMES,
  findActivePlan,
  findPlanById,
  listPlans,
  replaceSlots,
  updatePlanMeta,
} from '../data/cyclePlans.ts';
import { z } from 'zod';
import { listTasks } from '../data/tasks.ts';
import { listRooms } from '../data/rooms.ts';
import { activatePlan, applyProposal, discardProposal } from '../domain/activation.ts';
import { diffPlans } from '../domain/planDiff.ts';
import { slotsToDocs, validateSlotsAgainstDb } from '../domain/plans.ts';

const diffQuerySchema = z.object({ against: z.literal('active').optional() });
import { HttpError, notFound, parseOrThrow } from '../http/errors.ts';
import { parseIdParam } from '../http/params.ts';
import { toApi } from '../http/serialize.ts';
import { auditContext, requireActor } from '../identity/index.ts';

export const cyclePlanRoutes: FastifyPluginAsync = async (app) => {
  app.get('/cycle-plans', async () => toApi(await listPlans(app.deps.db)));

  app.get('/cycle-plans/active', async () => {
    const plan = await findActivePlan(app.deps.db);
    if (!plan) throw notFound('active plan');
    return toApi(plan);
  });

  app.get('/cycle-plans/:id', async (request) => {
    const plan = await findPlanById(app.deps.db, parseIdParam(request.params));
    if (!plan) throw notFound('cycle plan');
    return toApi(plan);
  });

  app.post('/cycle-plans', { preHandler: requireActor }, async (request, reply) => {
    const input = parseOrThrow(createCyclePlanInputSchema, request.body);
    let source = null;
    if (input.copyFromId) {
      source = await findPlanById(app.deps.db, new ObjectId(input.copyFromId));
      if (!source) throw notFound('cycle plan to copy');
    }
    const plan = await createPlan(
      auditContext(request),
      {
        name: input.name,
        active: false,
        slots: source?.slots ?? [],
        weekThemes: source?.weekThemes ?? EMPTY_WEEK_THEMES,
        draft: false,
        source: 'manual',
        proposalId: null,
        rationale: null,
        discarded: false,
      },
      source ? { copiedFrom: source._id } : undefined,
    );
    return reply.status(201).send(toApi(plan));
  });

  app.patch('/cycle-plans/:id', { preHandler: requireActor }, async (request) => {
    const id = parseIdParam(request.params);
    const input = parseOrThrow(updateCyclePlanInputSchema, request.body);
    const plan = await updatePlanMeta(auditContext(request), id, input);
    if (!plan) throw notFound('cycle plan');
    return toApi(plan);
  });

  app.delete('/cycle-plans/:id', { preHandler: requireActor }, async (request) => {
    const id = parseIdParam(request.params);
    const plans = await listPlans(app.deps.db);
    const plan = plans.find((candidate) => candidate._id.equals(id));
    if (!plan) throw notFound('cycle plan');
    if (plans[0]?._id.equals(id)) {
      throw new HttpError(409, 'default_plan', 'The default plan cannot be deleted');
    }
    if (plan.active) {
      throw new HttpError(409, 'active_plan', 'Activate another plan before deleting this plan');
    }
    if (!(await deletePlan(auditContext(request), id))) throw notFound('cycle plan');
    return { deleted: true };
  });

  app.post('/cycle-plans/:id/activate', { preHandler: requireActor }, async (request) => {
    const id = parseIdParam(request.params);
    return toApi(await activatePlan(auditContext(request), id));
  });

  /** Slot-level differences against the active plan, plus minutes per person per week before/after. */
  app.get('/cycle-plans/:id/diff', async (request) => {
    const id = parseIdParam(request.params);
    parseOrThrow(diffQuerySchema, request.query);
    const plan = await findPlanById(app.deps.db, id);
    if (!plan) throw notFound('cycle plan');
    const active = await findActivePlan(app.deps.db);
    const baseSlots = active?.slots ?? [];

    const [tasks, rooms] = await Promise.all([listTasks(app.deps.db), listRooms(app.deps.db)]);
    const roomNames = new Map(rooms.map((room) => [room._id.toHexString(), room.name]));
    const taskInfo = new Map(
      tasks.map((task) => [
        task._id.toHexString(),
        { name: task.name, roomName: roomNames.get(task.roomId.toHexString()) ?? null, durationMinutes: task.durationMinutes },
      ]),
    );
    const [before, after] = await Promise.all([
      validateSlotsAgainstDb(app.deps.db, baseSlots),
      validateSlotsAgainstDb(app.deps.db, plan.slots),
    ]);
    return {
      planId: plan._id.toHexString(),
      againstPlanId: active?._id.toHexString() ?? null,
      ...diffPlans(baseSlots, plan.slots, taskInfo),
      summary: { before: before.summary.weeks, after: after.summary.weeks },
      warnings: after.warnings,
    };
  });

  app.post('/cycle-plans/:id/apply-proposal', { preHandler: requireActor }, async (request) => {
    const id = parseIdParam(request.params);
    return toApi(await applyProposal(auditContext(request), id));
  });

  app.post('/cycle-plans/:id/discard', { preHandler: requireActor }, async (request) => {
    const id = parseIdParam(request.params);
    return toApi(await discardProposal(auditContext(request), id));
  });

  app.put('/cycle-plans/:id/slots', { preHandler: requireActor }, async (request) => {
    const id = parseIdParam(request.params);
    const input = parseOrThrow(putSlotsInputSchema, request.body);
    if (!(await findPlanById(app.deps.db, id))) throw notFound('cycle plan');

    const slots = slotsToDocs(input.slots);
    const validation = await validateSlotsAgainstDb(app.deps.db, slots);
    if (validation.errors.length > 0) {
      throw new HttpError(422, 'invalid_plan', 'Plan violates hard rules', validation);
    }

    const plan = await replaceSlots(auditContext(request), id, slots);
    if (!plan) throw notFound('cycle plan');
    return { plan: toApi(plan), warnings: validation.warnings, summary: validation.summary };
  });
};

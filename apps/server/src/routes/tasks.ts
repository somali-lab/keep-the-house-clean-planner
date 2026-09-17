import {
  bulkRoomTasksInputSchema,
  createTaskInputSchema,
  objectIdSchema,
  updateTaskInputSchema,
  fromDayKey,
  today,
} from '@huishoudplanner/shared';
import type { FastifyPluginAsync } from 'fastify';
import { z } from 'zod';
import { removeTaskFromPlans } from '../data/cyclePlans.ts';
import { findRoomById } from '../data/rooms.ts';
import { getSettings } from '../data/settings.ts';
import { updateUpcomingOccurrenceRoomSnapshots } from '../data/occurrences.ts';
import { bulkUpdateRoomTasks, createTask, deleteTask, findTaskById, listTasks, updateTask } from '../data/tasks.ts';
import { assertTaskReferences } from '../domain/tasks.ts';
import { notFound, parseOrThrow } from '../http/errors.ts';
import { booleanQuery, parseIdParam, toObjectId } from '../http/params.ts';
import { toApi } from '../http/serialize.ts';
import { auditContext, requirePlanner } from '../identity/index.ts';

const listQuerySchema = z.object({
  roomId: objectIdSchema.optional(),
  active: booleanQuery.optional(),
});

export const taskRoutes: FastifyPluginAsync = async (app) => {
  app.get('/tasks', async (request) => {
    const query = parseOrThrow(listQuerySchema, request.query);
    const tasks = await listTasks(app.deps.db, {
      ...(query.roomId ? { roomId: toObjectId(query.roomId) } : {}),
      ...(query.active === undefined ? {} : { active: query.active }),
    });
    return toApi(tasks);
  });

  app.post('/tasks', { preHandler: requirePlanner }, async (request, reply) => {
    const input = parseOrThrow(createTaskInputSchema, request.body);
    const roomId = toObjectId(input.roomId);
    const defaultAssigneeId = toObjectId(input.defaultAssigneeId);
    await assertTaskReferences(app.deps.db, { roomId, intervalKey: input.intervalKey, defaultAssigneeId });
    const task = await createTask(auditContext(request), {
      name: input.name,
      roomId,
      intervalKey: input.intervalKey,
      durationMinutes: input.durationMinutes,
      defaultAssigneeId,
      notes: input.notes,
      tags: input.tags,
    });
    return reply.status(201).send(toApi(task));
  });

  app.patch('/tasks/:id', { preHandler: requirePlanner }, async (request) => {
    const id = parseIdParam(request.params);
    const before = await findTaskById(app.deps.db, id);
    const input = parseOrThrow(updateTaskInputSchema, request.body);
    const { roomId, defaultAssigneeId, ...rest } = input;
    const patch = {
      ...rest,
      ...(roomId === undefined ? {} : { roomId: toObjectId(roomId) }),
      ...(defaultAssigneeId === undefined ? {} : { defaultAssigneeId: toObjectId(defaultAssigneeId) }),
    };
    await assertTaskReferences(app.deps.db, patch);
    const task = await updateTask(auditContext(request), id, patch);
    if (!task) throw notFound('task');
    if (before && roomId !== undefined && !before.roomId.equals(task.roomId)) {
      const [room, settings] = await Promise.all([
        findRoomById(app.deps.db, task.roomId),
        getSettings(app.deps.db),
      ]);
      if (room && settings) {
        await updateUpcomingOccurrenceRoomSnapshots(
          app.deps.db,
          task._id,
          fromDayKey(today(settings.timezone, app.deps.clock.now()), settings.timezone),
          room._id,
          room.name,
        );
      }
    }
    return toApi(task);
  });

  app.delete('/tasks/:id', { preHandler: requirePlanner }, async (request) => {
    const id = parseIdParam(request.params);
    const ctx = auditContext(request);
    await removeTaskFromPlans(ctx, id);
    const task = await deleteTask(ctx, id);
    if (!task) throw notFound('task');
    return { deleted: true };
  });

  app.post('/rooms/:id/tasks/bulk', { preHandler: requirePlanner }, async (request) => {
    const roomId = parseIdParam(request.params);
    const input = parseOrThrow(bulkRoomTasksInputSchema, request.body);
    if (!(await findRoomById(app.deps.db, roomId))) throw notFound('room');

    const ctx = auditContext(request);
    if (input.op === 'deactivate') {
      return { updated: await bulkUpdateRoomTasks(ctx, roomId, { active: false }) };
    }
    const defaultAssigneeId = toObjectId(input.defaultAssigneeId);
    await assertTaskReferences(app.deps.db, { defaultAssigneeId });
    return { updated: await bulkUpdateRoomTasks(ctx, roomId, { defaultAssigneeId }) };
  });
};

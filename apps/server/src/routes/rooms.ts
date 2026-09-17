import { createRoomInputSchema, updateRoomInputSchema } from '@huishoudplanner/shared';
import type { FastifyPluginAsync } from 'fastify';
import { createRoom, deleteRoom, findRoomById, listRooms, updateRoom } from '../data/rooms.ts';
import { countTasksInRoom } from '../data/tasks.ts';
import { HttpError, notFound, parseOrThrow } from '../http/errors.ts';
import { activeQuerySchema, parseIdParam } from '../http/params.ts';
import { toApi } from '../http/serialize.ts';
import { auditContext, requireAdmin } from '../identity/index.ts';

export const roomRoutes: FastifyPluginAsync = async (app) => {
  app.get('/rooms', async (request) => {
    const query = parseOrThrow(activeQuerySchema, request.query);
    return toApi(await listRooms(app.deps.db, query));
  });

  app.post('/rooms', { preHandler: requireAdmin }, async (request, reply) => {
    const input = parseOrThrow(createRoomInputSchema, request.body);
    const room = await createRoom(auditContext(request), input);
    return reply.status(201).send(toApi(room));
  });

  app.patch('/rooms/:id', { preHandler: requireAdmin }, async (request) => {
    const id = parseIdParam(request.params);
    const input = parseOrThrow(updateRoomInputSchema, request.body);
    const room = await updateRoom(auditContext(request), id, input);
    if (!room) throw notFound('room');
    return toApi(room);
  });

  app.delete('/rooms/:id', { preHandler: requireAdmin }, async (request) => {
    const id = parseIdParam(request.params);
    if (!(await findRoomById(app.deps.db, id))) throw notFound('room');
    const taskCount = await countTasksInRoom(app.deps.db, id);
    if (taskCount > 0) {
      throw new HttpError(
        409,
        'room_in_use',
        'Remove all tasks from this room before deleting it',
        { taskCount },
      );
    }
    const room = await deleteRoom(auditContext(request), id);
    if (!room) throw notFound('room');
    return { deleted: true };
  });
};

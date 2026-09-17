import { createUserInputSchema, updateUserInputSchema } from '@huishoudplanner/shared';
import type { FastifyPluginAsync } from 'fastify';
import { createUser, listUsers, updateUser } from '../data/users.ts';
import { notFound, parseOrThrow } from '../http/errors.ts';
import { activeQuerySchema, parseIdParam } from '../http/params.ts';
import { toApi } from '../http/serialize.ts';
import { auditContext, requireAdmin } from '../identity/index.ts';

export const userRoutes: FastifyPluginAsync = async (app) => {
  app.get('/users', async (request) => {
    const query = parseOrThrow(activeQuerySchema, request.query);
    return toApi(await listUsers(app.deps.db, query));
  });

  app.post('/users', { preHandler: requireAdmin }, async (request, reply) => {
    const input = parseOrThrow(createUserInputSchema, request.body);
    const user = await createUser(auditContext(request), input);
    return reply.status(201).send(toApi(user));
  });

  app.patch('/users/:id', { preHandler: requireAdmin }, async (request) => {
    const id = parseIdParam(request.params);
    const input = parseOrThrow(updateUserInputSchema, request.body);
    const user = await updateUser(auditContext(request), id, input);
    if (!user) throw notFound('user');
    return toApi(user);
  });
};

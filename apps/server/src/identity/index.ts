import type { FastifyPluginAsync, FastifyRequest } from 'fastify';
import { ObjectId } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { findUserById } from '../data/users.ts';
import { HttpError } from '../http/errors.ts';

/**
 * The only module that turns a request into an actor. The profile header is a
 * claim, not authentication; replacing this module is how real auth would be added.
 */

export interface Actor {
  actorId: ObjectId;
  source: 'ui' | 'api';
}

declare module 'fastify' {
  interface FastifyRequest {
    actor: Actor | null;
  }
}

const PROFILE_HEADER = 'x-profile-id';
const CLIENT_HEADER = 'x-client';
const OBJECT_ID_RE = /^[0-9a-f]{24}$/i;

const identity: FastifyPluginAsync = async (app) => {
  app.decorateRequest('actor', null);

  app.addHook('onRequest', async (request) => {
    request.actor = null;
    const raw = request.headers[PROFILE_HEADER];
    if (typeof raw !== 'string' || !OBJECT_ID_RE.test(raw)) return;
    const user = await findUserById(app.deps.db, new ObjectId(raw));
    if (!user?.active) return;
    request.actor = {
      actorId: user._id,
      source: request.headers[CLIENT_HEADER] === 'web' ? 'ui' : 'api',
    };
  });
};

// Same effect as fastify-plugin: decorators and hooks apply to the whole app.
export const identityPlugin = Object.assign(identity, { [Symbol.for('skip-override')]: true });

/** preHandler for every writing route. */
export async function requireActor(request: FastifyRequest): Promise<void> {
  if (!request.actor) {
    throw new HttpError(400, 'profile_required', 'An active profile is required (X-Profile-Id)');
  }
}

/** Audit context for the current request; throws profile_required without an actor. */
export function auditContext(request: FastifyRequest): AuditContext {
  const { actor } = request;
  if (!actor) {
    throw new HttpError(400, 'profile_required', 'An active profile is required (X-Profile-Id)');
  }
  const { db, clock } = request.server.deps;
  return { db, clock, log: request.log, actorId: actor.actorId, source: actor.source };
}

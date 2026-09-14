import type { AuditSource } from '@huishoudplanner/shared';
import type { FastifyBaseLogger } from 'fastify';
import { ObjectId, type Db } from 'mongodb';
import type { Clock } from '../clock.ts';

/** Actor for nightly jobs and seeding. */
export const SYSTEM_ACTOR_ID = new ObjectId('000000000000000000000000');

/** Everything a write needs: where to write, who did it, and how it arrived. */
export interface AuditContext {
  db: Db;
  clock: Clock;
  log: FastifyBaseLogger;
  actorId: ObjectId;
  source: AuditSource;
}

export function systemContext(deps: { db: Db; clock: Clock }, log: FastifyBaseLogger): AuditContext {
  return { db: deps.db, clock: deps.clock, log, actorId: SYSTEM_ACTOR_ID, source: 'system' };
}

import type { FastifyPluginAsync } from 'fastify';
import { runMorningNotify } from '../domain/notify/morning.ts';
import { toApi } from '../http/serialize.ts';
import { auditContext, requirePlanner } from '../identity/index.ts';
import { runNightly } from '../jobs/nightly.ts';

export const jobRoutes: FastifyPluginAsync = async (app) => {
  /** Runs the nightly generation on demand; audit entries carry the triggering profile. */
  app.post('/jobs/nightly', { preHandler: requirePlanner }, async (request) => {
    return toApi(await runNightly(auditContext(request)));
  });

  /** Sends the morning message now. Writes nothing; delivery failures are counted, not thrown. */
  app.post('/jobs/morning-notify', { preHandler: requirePlanner }, async (request) => {
    const { db, clock, notifier } = app.deps;
    return runMorningNotify({ db, clock, log: request.log, notifier });
  });

};

import { updateSettingsInputSchema } from '@huishoudplanner/shared';
import type { FastifyPluginAsync } from 'fastify';
import { getSettings, updateSettings } from '../data/settings.ts';
import { intervalKeysInUse } from '../data/tasks.ts';
import { removedIntervalKeys } from '../domain/intervals.ts';
import { HttpError, notFound, parseOrThrow } from '../http/errors.ts';
import { toApi } from '../http/serialize.ts';
import { auditContext, requireActor } from '../identity/index.ts';

export const settingsRoutes: FastifyPluginAsync = async (app) => {
  app.get('/settings', async () => {
    const settings = await getSettings(app.deps.db);
    if (!settings) throw notFound('settings');
    return toApi(settings);
  });

  app.patch('/settings', { preHandler: requireActor }, async (request) => {
    const input = parseOrThrow(updateSettingsInputSchema, request.body);
    const current = await getSettings(app.deps.db);
    if (!current) throw notFound('settings');

    if (input.intervals) {
      const removed = removedIntervalKeys(current.intervals, input.intervals);
      if (removed.length > 0) {
        const inUse = new Set(await intervalKeysInUse(app.deps.db));
        const blocked = removed.filter((key) => inUse.has(key));
        if (blocked.length > 0) {
          throw new HttpError(409, 'interval_in_use', 'Interval is used by tasks', { keys: blocked });
        }
      }
    }

    const settings = await updateSettings(auditContext(request), input);
    if (!settings) throw notFound('settings');
    return toApi(settings);
  });
};

import { today } from '@huishoudplanner/shared';
import type { FastifyPluginAsync } from 'fastify';
import { z } from 'zod';
import { getSettings } from '../data/settings.ts';
import { buildExport, importData, parseImport } from '../domain/transfer.ts';
import { HttpError, parseOrThrow } from '../http/errors.ts';
import { auditContext, requireAdmin } from '../identity/index.ts';

/** A household's full history easily exceeds Fastify's default 1 MB body limit. */
const IMPORT_BODY_LIMIT = 200 * 1024 * 1024;

const importQuerySchema = z.object({
  mode: z.literal('replace'),
  confirm: z.string().optional(),
});

export const transferRoutes: FastifyPluginAsync = async (app) => {
  app.get('/export/json', async (_request, reply) => {
    const { db, clock, config } = app.deps;
    const now = clock.now();
    const timezone = (await getSettings(db))?.timezone ?? config.timezone;
    const file = await buildExport(db, now);
    const stamp = today(timezone, now).replaceAll('-', '');
    return reply
      .header('Content-Disposition', `attachment; filename="huishoudplanner-${stamp}.json"`)
      .type('application/json')
      .send(file);
  });

  /** Replaces all data with the file. Requires `mode=replace&confirm=true`; validates everything first. */
  app.post('/import/json', { preHandler: requireAdmin, bodyLimit: IMPORT_BODY_LIMIT }, async (request) => {
    const query = parseOrThrow(importQuerySchema, request.query);
    if (query.confirm !== 'true') {
      throw new HttpError(400, 'confirmation_required', 'Importing replaces all data; add confirm=true');
    }
    const parsed = parseImport(request.body);
    return importData(auditContext(request), parsed);
  });
};

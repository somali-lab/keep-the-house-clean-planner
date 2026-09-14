import { applyPromotionInputSchema, dismissedPromotionSchema } from '@huishoudplanner/shared';
import type { FastifyPluginAsync } from 'fastify';
import { addDismissedPromotion } from '../data/settings.ts';
import { applyPromotion, computePromoteSuggestions } from '../domain/promote.ts';
import { parseOrThrow } from '../http/errors.ts';
import { toApi } from '../http/serialize.ts';
import { auditContext, requireActor } from '../identity/index.ts';

export const promoteRoutes: FastifyPluginAsync = async (app) => {
  app.get('/promote-suggestions', async () => computePromoteSuggestions(app.deps.db, app.deps.clock.now()));

  app.post('/promote-suggestions/apply', { preHandler: requireActor }, async (request) => {
    const input = parseOrThrow(applyPromotionInputSchema, request.body);
    const { plan, validation } = await applyPromotion(auditContext(request), input);
    return { plan: toApi(plan), warnings: validation.warnings, summary: validation.summary };
  });

  app.post('/promote-suggestions/dismiss', { preHandler: requireActor }, async (request) => {
    const input = parseOrThrow(dismissedPromotionSchema, request.body);
    await addDismissedPromotion(auditContext(request), input);
    return { dismissed: true };
  });
};

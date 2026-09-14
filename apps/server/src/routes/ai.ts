import {
  explainPlanInputSchema,
  proposePlanInputSchema,
  rebalanceInputSchema,
  suggestTasksInputSchema,
  testAiProviderInputSchema,
} from '@huishoudplanner/shared';
import type { FastifyPluginAsync } from 'fastify';
import { ObjectId } from 'mongodb';
import { findPlanById } from '../data/cyclePlans.ts';
import { getSettings } from '../data/settings.ts';
import { explainPlan, suggestTasks } from '../domain/ai/assist.ts';
import { AiProviderError } from '../domain/ai/provider.ts';
import { AI_CONNECTION_TEST_REQUEST, AI_CONNECTION_TEST_SCHEMA, getAiPromptCodeInfo } from '../domain/ai/prompt.ts';
import { generatePlanProposal, type ProposalResult } from '../domain/ai/proposals.ts';
import { notFound, parseOrThrow } from '../http/errors.ts';
import { auditContext, requireActor } from '../identity/index.ts';

function respond(result: ProposalResult) {
  return {
    planId: result.plan._id.toHexString(),
    proposalId: result.proposalId,
    warnings: result.warnings,
    rationale: result.rationale,
  };
}

/** AI assistant routes. Proposals are drafts; suggestions and explanations are not stored at all. */
export const aiRoutes: FastifyPluginAsync = async (app) => {
  async function provider() {
    const settings = await getSettings(app.deps.db);
    if (!settings) throw notFound('settings');
    return app.deps.aiProviderFor(settings.aiProvider);
  }

  app.get('/ai/prompt-info', async () => {
    const settings = await getSettings(app.deps.db);
    if (!settings) throw notFound('settings');
    return getAiPromptCodeInfo(settings.aiPrompts, settings.aiPromptTemplates);
  });

  app.post('/ai/test', { preHandler: requireActor }, async (request) => {
    const input = parseOrThrow(testAiProviderInputSchema, request.body);
    const testProvider = app.deps.aiProviderFor(input.aiProvider);
    const raw = await testProvider.completeJson({
      name: AI_CONNECTION_TEST_REQUEST,
      system: 'This is a connection test. Return one JSON object matching the schema and nothing else.',
      user: 'Return {"ok":true}.',
      schema: AI_CONNECTION_TEST_SCHEMA,
    });
    let answer: unknown;
    try {
      const fenced = /^\s*```(?:json)?\s*([\s\S]*?)\s*```\s*$/i.exec(raw);
      answer = JSON.parse(fenced ? fenced[1]! : raw);
    } catch {
      throw new AiProviderError('The AI connection worked, but the test answer was not valid JSON');
    }
    if (typeof answer !== 'object' || answer === null || !('ok' in answer) || answer.ok !== true) {
      throw new AiProviderError('The AI connection worked, but the test answer was not usable');
    }
    return { ok: true };
  });

  app.post('/ai/propose-plan', { preHandler: requireActor }, async (request) => {
    const input = parseOrThrow(proposePlanInputSchema, request.body ?? {});
    const result = await generatePlanProposal(auditContext(request), await provider(), {
      mode: 'propose',
      ...(input.taskIds ? { taskIds: input.taskIds.map((id) => new ObjectId(id)) } : {}),
      ...(input.constraints ? { constraints: input.constraints } : {}),
    });
    return respond(result);
  });

  app.post('/ai/rebalance', { preHandler: requireActor }, async (request) => {
    const input = parseOrThrow(rebalanceInputSchema, request.body);
    const basePlan = await findPlanById(app.deps.db, new ObjectId(input.planId));
    if (!basePlan) throw notFound('cycle plan');
    const result = await generatePlanProposal(auditContext(request), await provider(), {
      mode: 'rebalance',
      basePlan,
      ...(input.constraints ? { constraints: input.constraints } : {}),
    });
    return respond(result);
  });

  // Read-only AI actions still require a profile: they cost provider usage and should be attributable.
  app.post('/ai/suggest-tasks', { preHandler: requireActor }, async (request) => {
    const input = parseOrThrow(suggestTasksInputSchema, request.body);
    return { suggestions: await suggestTasks(app.deps.db, await provider(), new ObjectId(input.roomId)) };
  });

  app.post('/ai/explain', { preHandler: requireActor }, async (request) => {
    const input = parseOrThrow(explainPlanInputSchema, request.body);
    return { rationale: await explainPlan(app.deps.db, await provider(), new ObjectId(input.planId)) };
  });
};

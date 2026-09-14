import { randomUUID } from 'node:crypto';
import type { ObjectId } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { discardPlan, findPlanById, setActivePlan, type CyclePlanDoc } from '../data/cyclePlans.ts';
import { HttpError } from '../http/errors.ts';
import { replaceUpcomingOccurrences, type ReplacementResult } from './generation.ts';

export interface ActivationResult extends ReplacementResult {
  plan: CyclePlanDoc;
  runId: string;
}

/**
 * Makes the plan the only active one (audited as 'activate' by the actor), then
 * applies the replacement rule and regenerates. Deletions and generated
 * occurrences are recorded with source 'system' and the same runId.
 */
export async function activatePlan(ctx: AuditContext, planId: ObjectId): Promise<ActivationResult> {
  const runId = randomUUID();
  const plan = await setActivePlan(ctx, planId, { runId });
  if (!plan) throw new HttpError(404, 'not_found', 'cycle plan not found');
  const systemCtx: AuditContext = { ...ctx, source: 'system' };
  const result = await replaceUpcomingOccurrences(systemCtx, plan, runId);
  return { plan, runId, ...result };
}

async function requireDraft(ctx: AuditContext, planId: ObjectId): Promise<CyclePlanDoc> {
  const plan = await findPlanById(ctx.db, planId);
  if (!plan) throw new HttpError(404, 'not_found', 'cycle plan not found');
  if (!plan.draft || plan.discarded) {
    throw new HttpError(409, 'not_a_draft', 'Only an open AI draft can be applied or discarded');
  }
  return plan;
}

/**
 * Applies an AI draft through the same activation flow, but the activation is
 * recorded as 'ai-apply' with source 'ai' and the proposal id, so an AI-made
 * plan is always distinguishable. The plan stops being a draft.
 */
export async function applyProposal(ctx: AuditContext, planId: ObjectId): Promise<ActivationResult> {
  const draft = await requireDraft(ctx, planId);
  const runId = randomUUID();
  const aiCtx: AuditContext = { ...ctx, source: 'ai' };
  const plan = await setActivePlan(
    aiCtx,
    planId,
    { runId, proposalId: draft.proposalId },
    { action: 'ai-apply', changes: { draft: false } },
  );
  if (!plan) throw new HttpError(404, 'not_found', 'cycle plan not found');
  const result = await replaceUpcomingOccurrences({ ...ctx, source: 'system' }, plan, runId);
  return { plan, runId, ...result };
}

export async function discardProposal(ctx: AuditContext, planId: ObjectId): Promise<CyclePlanDoc> {
  await requireDraft(ctx, planId);
  const plan = await discardPlan(ctx, planId);
  if (!plan) throw new HttpError(404, 'not_found', 'cycle plan not found');
  return plan;
}

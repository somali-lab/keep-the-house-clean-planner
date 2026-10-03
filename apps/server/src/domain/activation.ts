import { randomUUID } from 'node:crypto';
import type { ObjectId } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { findPlanById, setActivePlan, type CyclePlanDoc } from '../data/cyclePlans.ts';
import { HttpError } from '../http/errors.ts';
import { activationPreview } from './activationPreview.ts';
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
export async function activatePlan(ctx: AuditContext, planId: ObjectId, previewToken: string): Promise<ActivationResult> {
  const fresh = await activationPreview(ctx, planId);
  if (fresh.previewToken !== previewToken) {
    throw new HttpError(409, 'stale_activation_preview', 'Activation preview is no longer current');
  }
  const runId = randomUUID();
  // An AI draft that is activated through the normal flow stops being a draft too, so it never reads as one later.
  const existing = await findPlanById(ctx.db, planId);
  const plan = await setActivePlan(ctx, planId, { runId }, existing?.draft ? { changes: { draft: false } } : {});
  if (!plan) throw new HttpError(404, 'not_found', 'cycle plan not found');
  const systemCtx: AuditContext = { ...ctx, source: 'system' };
  const result = await replaceUpcomingOccurrences(systemCtx, plan, runId);
  return { plan, runId, ...result };
}

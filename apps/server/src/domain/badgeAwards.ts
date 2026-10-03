import {
  evaluateBadgeRule,
  MAX_POINTS_CORRECTIONS,
  type BadgeExecution,
  type BadgeRule,
  type PointsRecomputeTrigger,
} from '@huishoudplanner/shared';
import { ObjectId } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { record } from '../audit/record.ts';
import {
  applyBadgeAwardChanges,
  BADGE_AWARDS_LEDGER_ID,
  badgeAwardKey,
  badgeAwardsCollection,
  listBadges,
  type AppliedBadgeAwardChange,
  type BadgeAwardChanges,
  type BadgeAwardDoc,
  type BadgeDoc,
  type BadgeRuleDoc,
} from '../data/badges.ts';
import { findCreditedExecutions } from '../data/occurrences.ts';
import { findWeekOnTimeBonuses } from '../data/points.ts';

/** What started an evaluation of all awards: a points reconciliation, a change of a badge, or a statistics reset. */
export type BadgeEvalTrigger = PointsRecomputeTrigger | 'badge' | 'reset';

export type BadgeEvalAudit =
  /** One audit entry per real change, with this reason (a live check-off, ADR-0014). */
  | { mode: 'each'; reason: string }
  /** One summary entry for the whole run, when anything changed. */
  | { mode: 'summary'; trigger: BadgeEvalTrigger };

export interface BadgeEvalResult {
  created: number;
  updated: number;
  removed: number;
}

/** The rule as the shared evaluator wants it: hex ids instead of ObjectIds. */
export function toBadgeRule(rule: BadgeRuleDoc): BadgeRule {
  return rule.type === 'onTimeWeeks'
    ? rule
    : { type: rule.type, taskIds: rule.taskIds.map((id) => id.toHexString()), threshold: rule.threshold };
}

const hex = (id: ObjectId): string => id.toHexString();

/**
 * Makes the awards of the given people (everybody when `personIds` is null) match the data (ADR-0014):
 * a person holds a badge exactly while the done executions credited to them, or their on-time week
 * bonuses, reach the threshold of an active badge. The award is dated at the moment the data first
 * crossed the threshold, so recomputing gives the same answer every time and never awards twice (the
 * key is unique per badge and person). Awards appear and disappear with the data: undoing work below
 * the threshold, deleting or deactivating a badge, or a statistics reset removes them.
 *
 * It does not take the points mutex itself: call it from inside `exclusively` (the points
 * reconciliation and the execution sync do) or through `reconcileBadges`. A run that changes nothing
 * writes and audits nothing.
 */
export async function evaluateBadgeAwards(ctx: AuditContext, personIds: ObjectId[] | null, audit: BadgeEvalAudit): Promise<BadgeEvalResult> {
  const result: BadgeEvalResult = { created: 0, updated: 0, removed: 0 };
  const badges = await listBadges(ctx.db, { active: true });
  const stored = await badgeAwardsCollection(ctx.db)
    .find(personIds ? { personId: { $in: personIds } } : {})
    .toArray();
  if (badges.length === 0 && stored.length === 0) return result;

  const executionsOf = new Map<string, BadgeExecution[]>();
  if (badges.some((badge) => badge.rule.type !== 'onTimeWeeks')) {
    for (const doc of await findCreditedExecutions(ctx.db, personIds)) {
      const list = executionsOf.get(hex(doc.personId)) ?? [];
      list.push({
        id: hex(doc._id),
        taskId: doc.taskId ? hex(doc.taskId) : null,
        minutes: doc.durationMinutesSnapshot,
        at: (doc.completedAt ?? doc.date).toISOString(),
      });
      executionsOf.set(hex(doc.personId), list);
    }
  }
  const onTimeOf = new Map<string, string[]>();
  if (badges.some((badge) => badge.rule.type === 'onTimeWeeks')) {
    for (const bonus of await findWeekOnTimeBonuses(ctx.db, personIds)) {
      const list = onTimeOf.get(hex(bonus.personId)) ?? [];
      list.push(bonus.date.toISOString());
      onTimeOf.set(hex(bonus.personId), list);
    }
  }

  const people = new Set<string>([...executionsOf.keys(), ...onTimeOf.keys(), ...(personIds ?? []).map(hex)]);
  const expected = new Map<string, { badge: BadgeDoc; personId: ObjectId; awardedAt: Date }>();
  for (const person of people) {
    for (const badge of badges) {
      const outcome = evaluateBadgeRule(toBadgeRule(badge.rule), executionsOf.get(person) ?? [], onTimeOf.get(person) ?? []);
      if (outcome.awardedAt === null) continue;
      const personId = new ObjectId(person);
      expected.set(badgeAwardKey(badge._id, personId), { badge, personId, awardedAt: new Date(outcome.awardedAt) });
    }
  }

  const changes: BadgeAwardChanges = { inserts: [], updates: [], deletes: [] };
  const storedByKey = new Map<string, BadgeAwardDoc>(stored.map((doc) => [doc.key, doc]));
  for (const [key, want] of expected) {
    const current = storedByKey.get(key);
    if (!current) changes.inserts.push({ badgeId: want.badge._id, personId: want.personId, awardedAt: want.awardedAt });
    else if (current.awardedAt.getTime() !== want.awardedAt.getTime()) changes.updates.push({ current, awardedAt: want.awardedAt });
  }
  for (const [key, current] of storedByKey) if (!expected.has(key)) changes.deletes.push(current);

  const applied = await applyBadgeAwardChanges(ctx, changes, audit.mode === 'each' ? { reason: audit.reason } : null);
  result.created = applied.filter((change) => change.change === 'created').length;
  result.updated = applied.filter((change) => change.change === 'updated').length;
  result.removed = applied.filter((change) => change.change === 'removed').length;
  if (audit.mode === 'summary' && applied.length > 0) await recordSummary(ctx, audit.trigger, result, applied);
  return result;
}

async function recordSummary(
  ctx: AuditContext,
  trigger: BadgeEvalTrigger,
  result: BadgeEvalResult,
  applied: AppliedBadgeAwardChange[],
): Promise<void> {
  const byKey = (a: AppliedBadgeAwardChange, b: AppliedBadgeAwardChange) => (a.key < b.key ? -1 : a.key > b.key ? 1 : 0);
  const changes = [...applied].sort(byKey).map((change) => ({
    key: change.key,
    badgeId: change.badgeId,
    personId: change.personId,
    change: change.change,
  }));
  await record(ctx, {
    entity: 'badgeAward',
    entityId: BADGE_AWARDS_LEDGER_ID,
    action: 'recompute',
    meta: {
      trigger,
      ...result,
      changes: changes.slice(0, MAX_POINTS_CORRECTIONS),
      changesTotal: changes.length,
      changesTruncated: changes.length > MAX_POINTS_CORRECTIONS,
    },
  });
}

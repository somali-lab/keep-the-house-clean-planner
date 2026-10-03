import { createHash } from 'node:crypto';
import {
  evaluateBadgeRule,
  EXAMPLE_BADGES,
  MAX_BADGES,
  MAX_BADGE_IMAGE_BYTES,
  sniffBadgeImageType,
  type AddExampleBadgesResponse,
  type Badge,
  type BadgeAward,
  type BadgeImageInput,
  type BadgeLanguage,
  type BadgeProgressResponse,
  type CreateBadgeInput,
  type UpdateBadgeInput,
} from '@huishoudplanner/shared';
import { Binary, ObjectId, type Db } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import {
  countBadges,
  createBadge,
  createExampleBadge,
  deleteBadge,
  findBadgeByExampleKey,
  listBadgeAwards,
  listBadges,
  removeTaskFromBadges,
  sortedIds,
  updateBadge,
  type BadgeAwardDoc,
  type BadgeDoc,
  type BadgeImageDoc,
  type BadgePatch,
  type BadgeRuleDoc,
} from '../data/badges.ts';
import { findCreditedExecutions } from '../data/occurrences.ts';
import { findWeekOnTimeBonuses } from '../data/points.ts';
import { listTasks, tasksCollection } from '../data/tasks.ts';
import { HttpError } from '../http/errors.ts';
import { toApi } from '../http/serialize.ts';
import { evaluateBadgeAwards, toBadgeRule, type BadgeEvalOptions, type BadgeEvalResult, type BadgeEvalTrigger } from './badgeAwards.ts';
import { exclusively } from './points.ts';

const validationError = (field: string, message: string) =>
  new HttpError(400, 'validation_error', 'Invalid request', [{ field, message }]);

const BASE64 = /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/;

/**
 * Checks the bytes of an image and returns what is stored (ADR-0014): valid base64, at most 256 KB, a
 * type that is really PNG, JPEG or WebP (an SVG is refused, because it can carry script) and the same as
 * the declared type. The hash identifies the bytes in the audit log.
 */
export function decodeBadgeImage(input: BadgeImageInput, field = 'image'): BadgeImageDoc {
  if (!BASE64.test(input.data)) throw validationError(`${field}.data`, 'invalid_base64');
  const bytes = Buffer.from(input.data, 'base64');
  if (bytes.length === 0) throw validationError(`${field}.data`, 'invalid_base64');
  if (bytes.length > MAX_BADGE_IMAGE_BYTES) throw validationError(`${field}.data`, 'image_too_large');
  const type = sniffBadgeImageType(bytes);
  if (!type) throw validationError(`${field}.data`, 'unsupported_image_type');
  if (type !== input.contentType) throw validationError(`${field}.contentType`, 'image_type_mismatch');
  return { data: new Binary(bytes), contentType: type, size: bytes.length, hash: createHash('sha256').update(bytes).digest('hex') };
}

/**
 * The rule is stored with its tasks deduplicated and in a stable order. Tasks that do not exist (a deleted one) are
 * dropped; a rule that named tasks and names none of them that exist is refused, because an empty list would silently
 * count every task.
 */
async function normalizeRule(db: Db, rule: CreateBadgeInput['rule'], field = 'rule'): Promise<BadgeRuleDoc> {
  if (rule.type === 'onTimeWeeks') return { type: 'onTimeWeeks', threshold: rule.threshold };
  const ids = [...new Set(rule.taskIds.map((id) => id.toLowerCase()))].sort();
  if (ids.length > 0) {
    const known = await tasksCollection(db)
      .find({ _id: { $in: ids.map((id) => new ObjectId(id)) } }, { projection: { _id: 1 } })
      .toArray();
    const existing = new Set(known.map((doc) => doc._id.toHexString()));
    const kept = ids.filter((id) => existing.has(id));
    if (kept.length === 0) throw validationError(`${field}.taskIds`, 'unknown_task');
    return { type: rule.type, taskIds: kept.map((id) => new ObjectId(id)), threshold: rule.threshold };
  }
  return { type: rule.type, taskIds: [], threshold: rule.threshold };
}

/** The API view of a badge: no bytes, only where the image is served from (ADR-0014). */
export function toBadgeView(doc: BadgeDoc): Badge {
  const id = doc._id.toHexString();
  return toApi({
    _id: doc._id,
    name: doc.name,
    description: doc.description,
    rule: doc.rule,
    active: doc.active,
    exampleKey: doc.exampleKey ?? null,
    image: doc.image
      ? { contentType: doc.image.contentType, size: doc.image.size, hash: doc.image.hash, url: `/api/badges/${id}/image?v=${doc.image.hash.slice(0, 12)}` }
      : null,
    createdAt: doc.createdAt,
    updatedAt: doc.updatedAt,
  }) as Badge;
}

export function toBadgeAwardView(doc: BadgeAwardDoc): BadgeAward {
  return toApi({ _id: doc._id, badgeId: doc.badgeId, personId: doc.personId, awardedAt: doc.awardedAt }) as BadgeAward;
}

/**
 * Evaluates every award again, inside the same queue as the points reconciliation and the execution
 * sync (ADR-0014). Run after a badge was created, changed in its rule or active flag, or deleted, and after a
 * statistics reset. A run that changes nothing writes and audits nothing.
 */
export function reconcileBadges(ctx: AuditContext, trigger: BadgeEvalTrigger, options: BadgeEvalOptions = {}): Promise<BadgeEvalResult> {
  return exclusively(ctx.db, () => evaluateBadgeAwards(ctx, null, { mode: 'summary', trigger }, options));
}

/**
 * The reconciliation for a caller whose own write is already committed (a badge change, a task deletion, a statistics
 * reset): a failure is logged and never fails that request; the nightly run repairs the awards (ADR-0014).
 */
export async function reconcileBadgesSafely(ctx: AuditContext, trigger: BadgeEvalTrigger, options: BadgeEvalOptions = {}): Promise<void> {
  try {
    await reconcileBadges(ctx, trigger, options);
  } catch (err) {
    ctx.log.error({ err, trigger }, 'badge reconciliation failed');
  }
}

async function assertRoomForBadge(db: Db): Promise<void> {
  if ((await countBadges(db)) >= MAX_BADGES) {
    throw new HttpError(409, 'badge_limit', `At most ${MAX_BADGES} badges can exist`, undefined, { limit: MAX_BADGES });
  }
}

/** Deleting a task removes it from the rules that name it; a rule left without tasks is deactivated (ADR-0014). */
export async function removeTaskFromBadgeRules(ctx: AuditContext, taskId: ObjectId): Promise<void> {
  if (await removeTaskFromBadges(ctx, taskId)) await reconcileBadgesSafely(ctx, 'badge');
}

export async function createBadgeFromInput(ctx: AuditContext, input: CreateBadgeInput): Promise<BadgeDoc> {
  await assertRoomForBadge(ctx.db);
  const rule = await normalizeRule(ctx.db, input.rule);
  const image = input.image ? decodeBadgeImage(input.image) : null;
  const doc = await createBadge(ctx, { name: input.name, description: input.description, rule, active: input.active, image });
  await reconcileBadgesSafely(ctx, 'badge');
  return doc;
}

/** Null when the badge does not exist. A change that changes nothing writes and audits nothing. */
export async function updateBadgeFromInput(ctx: AuditContext, id: ObjectId, input: UpdateBadgeInput): Promise<BadgeDoc | null> {
  const patch: BadgePatch = {};
  if (input.name !== undefined) patch.name = input.name;
  if (input.description !== undefined) patch.description = input.description;
  if (input.active !== undefined) patch.active = input.active;
  if (input.rule !== undefined) patch.rule = await normalizeRule(ctx.db, input.rule);
  if (input.image !== undefined) patch.image = input.image === null ? null : decodeBadgeImage(input.image);
  const result = await updateBadge(ctx, id, patch);
  if (!result) return null;
  if (result.affectsAwards) await reconcileBadgesSafely(ctx, 'badge');
  return result.after;
}

export async function deleteBadgeAndAwards(ctx: AuditContext, id: ObjectId): Promise<boolean> {
  const deleted = await deleteBadge(ctx, id);
  if (!deleted) return false;
  // The badge is gone, so its name travels along for the history of the awards that are withdrawn with it.
  await reconcileBadgesSafely(ctx, 'badge', { badgeNames: new Map([[id.toHexString(), deleted.name]]) });
  return true;
}

/**
 * Adds the example badges that do not exist yet (ADR-0014): idempotent by their stable key, so a second
 * call, or a badge an administrator renamed or deleted and re-added, never duplicates them. The tasks
 * of an example are found by name; an example without a matching task is created inactive, because a
 * rule without tasks would count every task.
 */
export async function addExampleBadges(ctx: AuditContext, language: BadgeLanguage): Promise<AddExampleBadgesResponse> {
  const tasks = await listTasks(ctx.db, { active: true });
  const created: BadgeDoc[] = [];
  let skipped = 0;
  for (const example of EXAMPLE_BADGES) {
    if (await findBadgeByExampleKey(ctx.db, example.key)) {
      skipped += 1;
      continue;
    }
    await assertRoomForBadge(ctx.db);
    const pattern = example.taskNamePattern ? new RegExp(example.taskNamePattern, 'i') : null;
    const matched = pattern ? tasks.filter((task) => pattern.test(task.name)).map((task) => task._id) : [];
    const rule: BadgeRuleDoc =
      example.rule.type === 'onTimeWeeks'
        ? { type: 'onTimeWeeks', threshold: example.rule.threshold }
        : { type: example.rule.type, taskIds: sortedIds(matched), threshold: example.rule.threshold };
    const doc = await createExampleBadge(ctx, {
      exampleKey: example.key,
      name: example.text[language].name,
      description: example.text[language].description,
      rule,
      active: pattern === null || matched.length > 0,
    });
    if (doc) created.push(doc);
    else skipped += 1;
  }
  if (created.length > 0) await reconcileBadgesSafely(ctx, 'badge');
  return { created: created.map(toBadgeView), skipped };
}

export async function listBadgeViews(db: Db, filter: { active?: boolean }): Promise<Badge[]> {
  return (await listBadges(db, filter)).map(toBadgeView);
}

export async function listBadgeAwardViews(db: Db, personId: ObjectId | undefined): Promise<BadgeAward[]> {
  const badges = new Set((await listBadges(db)).map((badge) => badge._id.toHexString()));
  // An award of a badge that was just deleted is removed by the reconciliation that follows; it is not shown meanwhile.
  return (await listBadgeAwards(db, personId ? { personId } : {})).filter((award) => badges.has(award.badgeId.toHexString())).map(toBadgeAwardView);
}

/** How far one person is towards every active badge, evaluated on the audited data (ADR-0014). */
export async function badgeProgressOf(db: Db, personId: ObjectId): Promise<BadgeProgressResponse> {
  const badges = await listBadges(db, { active: true });
  // Executions are only read when an active badge counts them.
  const needsExecutions = badges.some((badge) => badge.rule.type !== 'onTimeWeeks');
  const executions = (needsExecutions ? await findCreditedExecutions(db, [personId]) : []).map((doc) => ({
    id: doc._id.toHexString(),
    taskId: doc.taskId ? doc.taskId.toHexString() : null,
    minutes: doc.durationMinutesSnapshot,
    at: (doc.completedAt ?? doc.date).toISOString(),
  }));
  const onTime = (badges.some((badge) => badge.rule.type === 'onTimeWeeks') ? await findWeekOnTimeBonuses(db, [personId]) : []).map((bonus) => bonus.date.toISOString());
  return {
    personId: personId.toHexString(),
    items: badges.map((badge) => {
      const outcome = evaluateBadgeRule(toBadgeRule(badge.rule), executions, onTime);
      return { badgeId: badge._id.toHexString(), current: outcome.current, threshold: badge.rule.threshold, awardedAt: outcome.awardedAt };
    }),
  };
}

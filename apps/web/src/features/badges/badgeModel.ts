import type { Limits } from '../../api/v2/queries.ts';
import type { MessageKey } from '../../i18n/nl.ts';
import type { Badge, BadgeProgressItem, BadgeRule, BadgeRuleType, CreateBadgeBody, UpdateBadgeBody } from './api.ts';

export type BadgeLimits = Limits['badges'];

/** The picture types a badge accepts (the server's `badges.imageTypes`). */
export type BadgeImageType = string;

/** What the person did with the image in the editor: nothing, chose a new one, or removed it. */
export type ImageDraft =
  | { kind: 'keep' }
  | { kind: 'none' }
  | { kind: 'new'; contentType: BadgeImageType; data: string; previewUrl: string; fileName: string };

export interface BadgeForm {
  name: string;
  description: string;
  ruleType: BadgeRuleType;
  threshold: string;
  taskIds: string[];
  active: boolean;
  image: ImageDraft;
}

export type BadgeFormField = 'name' | 'description' | 'threshold' | 'image';
export type BadgeFormErrors = Partial<
  Record<BadgeFormField, 'badges.error.name' | 'badges.error.description' | 'badges.error.threshold' | 'badges.error.imageType' | 'badges.error.imageSize' | 'badges.error.imageRead'>
>;

export const EMPTY_BADGE_FORM: BadgeForm = {
  name: '',
  description: '',
  ruleType: 'executions',
  threshold: '10',
  taskIds: [],
  active: true,
  image: { kind: 'none' },
};

export function formFromBadge(badge: Badge): BadgeForm {
  return {
    name: badge.name,
    description: badge.description,
    ruleType: badge.rule.type,
    threshold: String(badge.rule.threshold),
    taskIds: badge.rule.type === 'onTimeWeeks' ? [] : [...badge.rule.taskIds],
    active: badge.active,
    image: badge.image ? { kind: 'keep' } : { kind: 'none' },
  };
}

/**
 * Whether a chosen file can be an image of a badge, judged by what the browser reports and by the limits of the server. The bytes
 * themselves are checked by the server, which answers `unsupported_image_type` for a file that only pretends to be an image.
 */
export function checkImageFile(
  file: { type: string; size: number },
  limits: Pick<BadgeLimits, 'imageTypes' | 'maxImageBytes'>,
): 'badges.error.imageType' | 'badges.error.imageSize' | null {
  if (!limits.imageTypes.includes(file.type)) return 'badges.error.imageType';
  if (file.size > limits.maxImageBytes) return 'badges.error.imageSize';
  return null;
}

/** Reads a chosen file as base64 after the early checks of `checkImageFile`. */
export async function readImageFile(
  file: File,
  limits: Pick<BadgeLimits, 'imageTypes' | 'maxImageBytes'>,
): Promise<{ ok: true; contentType: BadgeImageType; data: string } | { ok: false; error: NonNullable<ReturnType<typeof checkImageFile>> | 'badges.error.imageRead' }> {
  const early = checkImageFile(file, limits);
  if (early) return { ok: false, error: early };
  let data: string;
  try {
    data = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader();
      reader.onerror = () => reject(reader.error);
      reader.onload = () => {
        const text = String(reader.result);
        const comma = text.indexOf(',');
        if (comma < 0) reject(new Error('unexpected data url'));
        else resolve(text.slice(comma + 1));
      };
      reader.readAsDataURL(file);
    });
  } catch {
    return { ok: false, error: 'badges.error.imageRead' };
  }
  return { ok: true, contentType: file.type, data };
}

export function maxThreshold(ruleType: BadgeRuleType, limits: Pick<BadgeLimits, 'maxThreshold' | 'maxOnTimeWeeksThreshold'>): number {
  return ruleType === 'onTimeWeeks' ? limits.maxOnTimeWeeksThreshold : limits.maxThreshold;
}

/** A whole number of 1 or more from the text of the threshold field, or null. */
export function parseThreshold(text: string, ruleType: BadgeRuleType, limits: Pick<BadgeLimits, 'maxThreshold' | 'maxOnTimeWeeksThreshold'>): number | null {
  const trimmed = text.trim();
  if (!/^\d{1,9}$/.test(trimmed)) return null;
  const value = Number(trimmed);
  return value >= 1 && value <= maxThreshold(ruleType, limits) ? value : null;
}

export type BadgeSave =
  | { ok: true; create: CreateBadgeBody; patch: UpdateBadgeBody }
  | { ok: false; errors: BadgeFormErrors };

/**
 * Checks the editor against what the server accepts and builds the request: the whole badge for a new one, and for an
 * existing one only the image when it was changed (a kept image is left out, a removed one is null: the one place where the API
 * documents an explicit null). Optional keys are never sent as null.
 */
export function buildBadgeSave(form: BadgeForm, limits: BadgeLimits): BadgeSave {
  const errors: BadgeFormErrors = {};
  const name = form.name.trim();
  if (name.length < 1 || name.length > limits.maxNameLength) errors.name = 'badges.error.name';
  const description = form.description.trim();
  if (description.length > limits.maxDescriptionLength) errors.description = 'badges.error.description';
  const threshold = parseThreshold(form.threshold, form.ruleType, limits);
  if (threshold === null) errors.threshold = 'badges.error.threshold';
  if (Object.keys(errors).length > 0 || threshold === null) return { ok: false, errors };

  const rule: CreateBadgeBody['rule'] =
    form.ruleType === 'onTimeWeeks' ? { type: 'onTimeWeeks', threshold } : { type: form.ruleType, taskIds: [...form.taskIds].sort(), threshold };
  const image = form.image.kind === 'new' ? { contentType: form.image.contentType, data: form.image.data } : undefined;
  const create: CreateBadgeBody = { name, description, rule, active: form.active, ...(image ? { image } : {}) };
  const patch: UpdateBadgeBody = {
    name,
    description,
    rule,
    active: form.active,
    ...(form.image.kind === 'new' ? { image } : form.image.kind === 'none' ? { image: null } : {}),
  };
  return { ok: true, create, patch };
}

/** The progress of a person as "7/10", never above the threshold. */
export function progressText(current: number, threshold: number): string {
  return `${Math.min(current, threshold)}/${threshold}`;
}

export interface RuleText {
  key: Extract<MessageKey, 'badges.rule.executions' | 'badges.rule.minutes' | 'badges.rule.onTimeWeeks'>;
  count: number;
  /** Task names for the executions and minutes rules; empty means every task. */
  tasks: string[];
}

const MAX_NAMED_TASKS = 3;

/** What a rule counts, with the names of its tasks (unknown tasks are left out, so a deleted task never shows as an id). */
export function ruleText(rule: BadgeRule, taskNames: ReadonlyMap<string, string>): RuleText {
  if (rule.type === 'onTimeWeeks') return { key: 'badges.rule.onTimeWeeks', count: rule.threshold, tasks: [] };
  return {
    key: rule.type === 'executions' ? 'badges.rule.executions' : 'badges.rule.minutes',
    count: rule.threshold,
    tasks: rule.taskIds.flatMap((id) => {
      const name = taskNames.get(id);
      return name ? [name] : [];
    }),
  };
}

/** Names of at most three tasks, then "and N more". */
export function limitedNames(names: string[]): { shown: string[]; more: number } {
  return { shown: names.slice(0, MAX_NAMED_TASKS), more: Math.max(0, names.length - MAX_NAMED_TASKS) };
}

export interface BadgeProgressView {
  badge: Badge;
  current: number;
  threshold: number;
  awardedAt: string | null;
}

/** Earned badges first, by the moment they were earned, then the others by how close they are; active badges only. */
export function badgesWithProgress(badges: Badge[], items: BadgeProgressItem[]): BadgeProgressView[] {
  const byBadge = new Map(items.map((item) => [item.badgeId, item]));
  const views = badges
    .filter((badge) => badge.active)
    .map((badge) => {
      const item = byBadge.get(badge.id);
      return { badge, current: item?.current ?? 0, threshold: item?.threshold ?? badge.rule.threshold, awardedAt: item?.awardedAt ?? null };
    });
  const earned = views.filter((view) => view.awardedAt !== null).sort((a, b) => a.awardedAt!.localeCompare(b.awardedAt!));
  const open = views
    .filter((view) => view.awardedAt === null)
    .sort((a, b) => Math.min(b.current / b.threshold, 1) - Math.min(a.current / a.threshold, 1) || a.badge.name.localeCompare(b.badge.name));
  return [...earned, ...open];
}

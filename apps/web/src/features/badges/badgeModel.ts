import type { Badge, BadgeProgressItem, CreateBadgeInput, UpdateBadgeInput } from '@huishoudplanner/shared';
import {
  BADGE_IMAGE_TYPES,
  MAX_BADGE_DESCRIPTION_LENGTH,
  MAX_BADGE_IMAGE_BYTES,
  MAX_BADGE_NAME_LENGTH,
  MAX_BADGE_THRESHOLD,
  MAX_ON_TIME_WEEKS_THRESHOLD,
  sniffBadgeImageType,
  type BadgeImageType,
  type BadgeRuleType,
} from '@huishoudplanner/shared/badges';
import type { MessageKey } from '../../i18n/nl.ts';

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

/** Whether a chosen file can be an image of a badge, judged by what the browser reports; the real bytes are checked on reading. */
export function checkImageFile(file: { type: string; size: number }): 'badges.error.imageType' | 'badges.error.imageSize' | null {
  if (!(BADGE_IMAGE_TYPES as readonly string[]).includes(file.type)) return 'badges.error.imageType';
  if (file.size > MAX_BADGE_IMAGE_BYTES) return 'badges.error.imageSize';
  return null;
}

/** Decodes base64 into bytes. */
function fromBase64(data: string): Uint8Array {
  const text = atob(data);
  return Uint8Array.from(text, (char) => char.charCodeAt(0));
}

/**
 * Reads a chosen file as base64, and checks the first bytes against what the file says it is: a text file renamed to
 * .png is refused here, like the server would refuse it (an SVG never passes).
 */
export async function readImageFile(
  file: File,
): Promise<{ ok: true; contentType: BadgeImageType; data: string } | { ok: false; error: NonNullable<ReturnType<typeof checkImageFile>> | 'badges.error.imageRead' }> {
  const early = checkImageFile(file);
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
  const type = sniffBadgeImageType(fromBase64(data));
  if (type === null || type !== file.type) return { ok: false, error: 'badges.error.imageType' };
  return { ok: true, contentType: type, data };
}

export function maxThreshold(ruleType: BadgeRuleType): number {
  return ruleType === 'onTimeWeeks' ? MAX_ON_TIME_WEEKS_THRESHOLD : MAX_BADGE_THRESHOLD;
}

/** A whole number of 1 or more from the text of the threshold field, or null. */
export function parseThreshold(text: string, ruleType: BadgeRuleType): number | null {
  const trimmed = text.trim();
  if (!/^\d{1,9}$/.test(trimmed)) return null;
  const value = Number(trimmed);
  return value >= 1 && value <= maxThreshold(ruleType) ? value : null;
}

export type BadgeSave =
  | { ok: true; create: CreateBadgeInput; patch: UpdateBadgeInput }
  | { ok: false; errors: BadgeFormErrors };

/**
 * Checks the editor against what the server accepts and builds the request: the whole badge for a new one, and for an
 * existing one only the image when it was changed (a kept image is left out, a removed one is null).
 */
export function buildBadgeSave(form: BadgeForm): BadgeSave {
  const errors: BadgeFormErrors = {};
  const name = form.name.trim();
  if (name.length < 1 || name.length > MAX_BADGE_NAME_LENGTH) errors.name = 'badges.error.name';
  const description = form.description.trim();
  if (description.length > MAX_BADGE_DESCRIPTION_LENGTH) errors.description = 'badges.error.description';
  const threshold = parseThreshold(form.threshold, form.ruleType);
  if (threshold === null) errors.threshold = 'badges.error.threshold';
  if (Object.keys(errors).length > 0 || threshold === null) return { ok: false, errors };

  const rule: CreateBadgeInput['rule'] =
    form.ruleType === 'onTimeWeeks' ? { type: 'onTimeWeeks', threshold } : { type: form.ruleType, taskIds: [...form.taskIds].sort(), threshold };
  const image = form.image.kind === 'new' ? { contentType: form.image.contentType, data: form.image.data } : undefined;
  const create: CreateBadgeInput = { name, description, rule, active: form.active, ...(image ? { image } : {}) };
  const patch: UpdateBadgeInput = {
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
export function ruleText(rule: Badge['rule'], taskNames: ReadonlyMap<string, string>): RuleText {
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
      const item = byBadge.get(badge._id);
      return { badge, current: item?.current ?? 0, threshold: item?.threshold ?? badge.rule.threshold, awardedAt: item?.awardedAt ?? null };
    });
  const earned = views.filter((view) => view.awardedAt !== null).sort((a, b) => a.awardedAt!.localeCompare(b.awardedAt!));
  const open = views
    .filter((view) => view.awardedAt === null)
    .sort((a, b) => Math.min(b.current / b.threshold, 1) - Math.min(a.current / a.threshold, 1) || a.badge.name.localeCompare(b.badge.name));
  return [...earned, ...open];
}

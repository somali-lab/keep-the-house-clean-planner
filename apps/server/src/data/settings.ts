import type {
  AiProviderSettings,
  AiPromptTemplates,
  AiPrompts,
  CompletionControl,
  DismissedPromotion,
  Interval,
  UpdateSettingsInput,
  VacationRange,
} from '@huishoudplanner/shared';
import { ObjectId, type Db } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { diffFields, isEmptyDiff } from '../audit/diff.ts';
import { record } from '../audit/record.ts';
import { COLLECTIONS } from './db.ts';

/** Singleton document id; also the audit entityId for settings. */
export const SETTINGS_ID = new ObjectId('000000000000000000000001');

export interface SettingsDoc {
  _id: ObjectId;
  /** Day key 'YYYY-MM-DD' of a Monday. */
  cycleAnchorDate: string;
  weekStartsOn: 1;
  timezone: string;
  vacationRanges: VacationRange[];
  intervals: Interval[];
  aiProvider: AiProviderSettings;
  aiPrompts?: AiPrompts;
  aiPromptTemplates?: AiPromptTemplates;
  completionControl?: CompletionControl;
  promoteThreshold: number;
  dismissedPromotions: DismissedPromotion[];
  createdAt: Date;
  updatedAt: Date;
}

const settingsCollection = (db: Db) => db.collection<SettingsDoc>(COLLECTIONS.settings);

export function getSettings(db: Db): Promise<SettingsDoc | null> {
  return settingsCollection(db).findOne({ _id: SETTINGS_ID });
}

/** Idempotent: inserts only when no settings exist; audits only an actual insert. */
export async function insertSettingsIfMissing(
  ctx: AuditContext,
  values: Omit<SettingsDoc, '_id' | 'createdAt' | 'updatedAt'>,
): Promise<boolean> {
  const now = ctx.clock.now();
  const doc: SettingsDoc = { _id: SETTINGS_ID, ...values, createdAt: now, updatedAt: now };
  const result = await settingsCollection(ctx.db).updateOne(
    { _id: SETTINGS_ID },
    { $setOnInsert: doc },
    { upsert: true },
  );
  if (result.upsertedCount !== 1) return false;
  const { after } = diffFields({}, { ...doc }, { ignore: ['_id', 'createdAt', 'updatedAt'] });
  await record(ctx, { entity: 'settings', entityId: SETTINGS_ID, action: 'create', after });
  return true;
}

/** Returns null if settings are missing; skips the write (and audit) when nothing changes. */
export async function updateSettings(
  ctx: AuditContext,
  patch: UpdateSettingsInput & { dismissedPromotions?: DismissedPromotion[] },
): Promise<SettingsDoc | null> {
  const before = await getSettings(ctx.db);
  if (!before) return null;
  const diff = diffFields({ ...before }, { ...before, ...patch });
  if (isEmptyDiff(diff)) return before;

  const after = await settingsCollection(ctx.db).findOneAndUpdate(
    { _id: SETTINGS_ID },
    { $set: { ...patch, updatedAt: ctx.clock.now() } },
    { returnDocument: 'after' },
  );
  if (!after) return null;
  await record(ctx, { entity: 'settings', entityId: SETTINGS_ID, action: 'update', ...diff });
  return after;
}

/** Adds a shipped interval to existing settings without changing other intervals. */
export async function addIntervalIfMissing(ctx: AuditContext, interval: Interval, beforeKey: string): Promise<boolean> {
  const settings = await getSettings(ctx.db);
  if (!settings || settings.intervals.some((item) => item.key === interval.key)) return false;

  const intervals = [...settings.intervals];
  const index = intervals.findIndex((item) => item.key === beforeKey);
  intervals.splice(index === -1 ? intervals.length : index, 0, interval);
  return (await updateSettings(ctx, { intervals })) !== null;
}

/**
 * Remembers a dismissed promote suggestion. A dismissal for the same slot and
 * target replaces the earlier one, so only the newest evidence id counts.
 */
export async function addDismissedPromotion(ctx: AuditContext, entry: DismissedPromotion): Promise<SettingsDoc | null> {
  const current = await getSettings(ctx.db);
  if (!current) return null;
  const sameTarget = (d: DismissedPromotion) =>
    d.planId === entry.planId &&
    d.taskId === entry.taskId &&
    d.weekIndex === entry.weekIndex &&
    d.weekday === entry.weekday &&
    d.toWeekday === entry.toWeekday &&
    d.toAssigneeId === entry.toAssigneeId;
  return updateSettings(ctx, {
    dismissedPromotions: [...current.dismissedPromotions.filter((d) => !sameTarget(d)), entry],
  });
}

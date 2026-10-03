import type {
  AiProviderSettings,
  BonusScheduleRow,
  AiPromptTemplates,
  AiPrompts,
  CompletionControl,
  DismissedPromotion,
  Interval,
  RewardGoals,
  UpdateSettingsInput,
  VacationRange,
} from '@huishoudplanner/shared';
import { ObjectId, type Db } from 'mongodb';
import type { AuditContext } from '../audit/context.ts';
import { deepEqual, diffFields, isEmptyDiff } from '../audit/diff.ts';
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
  /** Bonus amounts over time (ADR-0012), sorted by `from`; missing means no bonuses. */
  bonusSchedule?: BonusScheduleRow[];
  /** Boundary of the last statistics reset: periods that start before this day earn no bonus (ADR-0012). */
  bonusFloor?: string;
  /** ISO 4217 currency points are converted to (requirements 4.12); missing means EUR. */
  currencyCode?: string;
  /** Cents one point is worth (requirements 4.12), 0 to 10000; missing means 0, no money shown. */
  centsPerPoint?: number;
  /** Goals of the reward meter (requirements 4.12); missing means both are automatic. */
  rewardGoals?: RewardGoals;
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

/** The bonus schedule changed between reading it and writing the new one (ADR-0012); nothing was written. */
export class StaleBonusScheduleError extends Error {
  constructor() {
    super('The bonus schedule changed in the meantime');
    this.name = 'StaleBonusScheduleError';
  }
}

/**
 * Returns null if settings are missing; skips the write (and audit) when nothing changes. A
 * `bonusSchedule` patch is audited against the schedule that was in force, `[]` when there was none.
 * With `options.basedOnBonusSchedule` the write is a compare-and-set: it only happens while the stored
 * schedule is still the one the patch was computed from (`undefined` = none stored), otherwise
 * {@link StaleBonusScheduleError} is thrown and nothing is written.
 */
export async function updateSettings(
  ctx: AuditContext,
  patch: Omit<UpdateSettingsInput, 'periodBonuses'> & { dismissedPromotions?: DismissedPromotion[]; bonusSchedule?: BonusScheduleRow[] },
  options: { basedOnBonusSchedule?: { rows: BonusScheduleRow[] | undefined } } = {},
): Promise<SettingsDoc | null> {
  const before = await getSettings(ctx.db);
  if (!before) return null;
  const based = options.basedOnBonusSchedule;
  if (based && !deepEqual(before.bonusSchedule, based.rows)) throw new StaleBonusScheduleError();
  const diff = diffFields(
    { ...before, ...(patch.bonusSchedule ? { bonusSchedule: before.bonusSchedule ?? [] } : {}) },
    { ...before, ...patch },
  );
  if (isEmptyDiff(diff)) return before;

  const after = await settingsCollection(ctx.db).findOneAndUpdate(
    {
      _id: SETTINGS_ID,
      ...(based ? { bonusSchedule: based.rows === undefined ? { $exists: false } : based.rows } : {}),
    },
    { $set: { ...patch, updatedAt: ctx.clock.now() } },
    { returnDocument: 'after' },
  );
  if (!after) {
    if (based) throw new StaleBonusScheduleError();
    return null;
  }
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

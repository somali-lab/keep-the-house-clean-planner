import { z } from 'zod';
import { MAX_BONUS_POINTS, MIN_BONUS_POINTS } from '../bonuses.ts';
import { isTwoDecimalCurrency, MAX_CENTS_PER_POINT, MIN_CENTS_PER_POINT } from '../points.ts';
import { MAX_REWARD_GOAL_POINTS, MIN_REWARD_GOAL_POINTS } from '../rewards.ts';
import { isMonday } from '../time.ts';
import { dayKeySchema, objectIdSchema, timestampsSchema, weekdaySchema } from './common.ts';
import { intervalSchema } from './intervals.ts';

export const anchorDateSchema = dayKeySchema.refine(isMonday, 'anchor_not_monday');

export const vacationRangeSchema = z
  .object({ from: dayKeySchema, to: dayKeySchema })
  .refine((r) => r.from <= r.to, { message: 'vacation_range_inverted', path: ['to'] });
export type VacationRange = z.infer<typeof vacationRangeSchema>;

export const aiProviderTypeSchema = z.enum(['none', 'mock', 'anthropic', 'openai-compatible', 'ollama']);
export type AiProviderType = z.infer<typeof aiProviderTypeSchema>;

export const DEFAULT_AI_TIMEOUT_SECONDS = 180;
export const MIN_AI_TIMEOUT_SECONDS = 10;
export const MAX_AI_TIMEOUT_SECONDS = 900;

export const aiProviderSettingsSchema = z.object({
  type: aiProviderTypeSchema,
  endpoint: z.string().url().optional(),
  model: z.string().min(1).optional(),
  timeoutSeconds: z.number().int().min(MIN_AI_TIMEOUT_SECONDS).max(MAX_AI_TIMEOUT_SECONDS).optional(),
});
export type AiProviderSettings = z.infer<typeof aiProviderSettingsSchema>;

export const aiPromptsSchema = z.object({
  planProposal: z.string().max(8000),
  planRebalance: z.string().max(8000),
  taskSuggestions: z.string().max(8000),
  planExplanation: z.string().max(8000),
});
export type AiPrompts = z.infer<typeof aiPromptsSchema>;
export const DEFAULT_AI_PROMPTS: AiPrompts = {
  planProposal:
    'Maak een praktisch vierwekenplan voor alle taken. Verdeel de minuten zo eerlijk mogelijk, houd rekening met beschikbaarheid en daglimieten en spreid herhalingen logisch.',
  planRebalance:
    'Herverdeel het actieve plan alleen waar dat de balans, spreiding of ingestelde limieten verbetert. Behoud bestaande dagen en uitvoerders wanneer wijzigen geen duidelijke verbetering geeft.',
  taskSuggestions:
    'Stel alleen nuttige huishoudelijke taken voor die voor deze ruimte nog ontbreken. Gebruik korte, concrete Nederlandse taaknamen en realistische tijdsduren.',
  planExplanation:
    'Leg het plan in eenvoudig Nederlands uit. Benoem per week de verdeling, opvallend drukke momenten en waarom de planning redelijk verdeeld is.',
};
export const EMPTY_AI_PROMPTS: AiPrompts = {
  planProposal: '',
  planRebalance: '',
  taskSuggestions: '',
  planExplanation: '',
};

export const aiPromptTemplateSchema = z.object({
  system: z.string().min(1).max(20000).refine((value) => value.includes('{{schema}}'), 'schema_placeholder_required'),
  user: z.string().min(1).max(20000).refine((value) => value.includes('{{input}}'), 'input_placeholder_required'),
});
export type AiPromptTemplate = z.infer<typeof aiPromptTemplateSchema>;
export const aiPromptTemplatesSchema = z.object({
  planProposal: aiPromptTemplateSchema,
  planRebalance: aiPromptTemplateSchema,
  taskSuggestions: aiPromptTemplateSchema,
  planExplanation: aiPromptTemplateSchema,
});
export type AiPromptTemplates = z.infer<typeof aiPromptTemplatesSchema>;

export const completionControlSchema = z.enum(['circle', 'thumb']);
export type CompletionControl = z.infer<typeof completionControlSchema>;

export const dismissedPromotionSchema = z.object({
  planId: objectIdSchema,
  taskId: objectIdSchema,
  weekIndex: z.number().int().min(0).max(3),
  weekday: weekdaySchema,
  toWeekday: weekdaySchema,
  toAssigneeId: objectIdSchema.nullable(),
  /** Newest evidence occurrence id at dismissal; newer evidence re-surfaces the suggestion. */
  lastEvidenceId: objectIdSchema,
});
export type DismissedPromotion = z.infer<typeof dismissedPromotionSchema>;

const uniqueIntervalKeys = (intervals: { key: string }[]) =>
  new Set(intervals.map((i) => i.key)).size === intervals.length;

/** One amount of a week or cycle bonus: an integer from 0 to 1000; 0 disables that kind (ADR-0012). */
export const bonusAmountSchema = z.number().int().min(MIN_BONUS_POINTS).max(MAX_BONUS_POINTS);

export const bonusAmountsSchema = z.object({
  weekDone: bonusAmountSchema,
  weekOnTime: bonusAmountSchema,
  cycleDone: bonusAmountSchema,
  cycleOnTime: bonusAmountSchema,
});

/** The amounts that apply to every period whose last day is on or after `from`, until the next row. */
export const bonusScheduleRowSchema = bonusAmountsSchema.extend({ from: dayKeySchema });
export type BonusScheduleRowInput = z.infer<typeof bonusScheduleRowSchema>;

/** Sorted by `from`, with unique `from` days. */
export const bonusScheduleSchema = z
  .array(bonusScheduleRowSchema)
  .refine((rows) => rows.every((row, i) => i === 0 || rows[i - 1]!.from < row.from), 'bonus_schedule_not_sorted');

/**
 * ISO 4217 code of the currency points are converted to (requirements 4.12): three capitals that the runtime knows as a
 * currency and that has exactly two fraction digits, because money is whole cents. A currency with another number
 * of digits (JPY, KWD) is refused with its own message.
 */
export const currencyCodeSchema = z
  .string()
  .regex(/^[A-Z]{3}$/, 'invalid_currency_code')
  .refine((code) => {
    // Without Intl.supportedValuesOf (older runtimes) the three-capital shape is all that can be checked.
    const supported = (Intl as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
    return supported ? supported('currency').includes(code) : true;
  }, 'invalid_currency_code')
  .refine((code) => !/^[A-Z]{3}$/.test(code) || !isKnownCurrency(code) || isTwoDecimalCurrency(code), 'currency_not_two_decimals');

function isKnownCurrency(code: string): boolean {
  const supported = (Intl as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
  return supported ? supported('currency').includes(code) : true;
}

/** Cents of currency one point is worth: an integer from 0 to 10000; 0 shows no money (requirements 4.12). */
export const centsPerPointSchema = z.number().int().min(MIN_CENTS_PER_POINT).max(MAX_CENTS_PER_POINT);

/** One goal of the reward meter: an integer from 0 to 100000 points, or null for the automatic goal (requirements 4.12). */
export const rewardGoalPointsSchema = z.number().int().min(MIN_REWARD_GOAL_POINTS).max(MAX_REWARD_GOAL_POINTS).nullable();

export const rewardGoalsSchema = z.object({
  weekPoints: rewardGoalPointsSchema,
  cyclePoints: rewardGoalPointsSchema,
});

export const settingsSchema = z
  .object({
    cycleAnchorDate: anchorDateSchema,
    weekStartsOn: z.literal(1),
    timezone: z.string().min(1),
    vacationRanges: z.array(vacationRangeSchema),
    intervals: z.array(intervalSchema).refine(uniqueIntervalKeys, 'duplicate_interval_key'),
    aiProvider: aiProviderSettingsSchema,
    aiPrompts: aiPromptsSchema.optional(),
    aiPromptTemplates: aiPromptTemplatesSchema.optional(),
    completionControl: completionControlSchema.optional(),
    promoteThreshold: z.number().int().min(2),
    dismissedPromotions: z.array(dismissedPromotionSchema),
    /** Bonus amounts over time (ADR-0012); a missing list means no bonuses. The API always returns it. */
    bonusSchedule: bonusScheduleSchema.optional(),
    /** Boundary of the last statistics reset: periods that start before this day earn no bonus (ADR-0012). */
    bonusFloor: dayKeySchema.optional(),
    /** Currency of the conversion (requirements 4.12); a missing value means EUR. The API always returns it. */
    currencyCode: currencyCodeSchema.optional(),
    /** Cents one point is worth (requirements 4.12); a missing value means 0, no money shown. The API always returns it. */
    centsPerPoint: centsPerPointSchema.optional(),
    /** Goals of the reward meter (requirements 4.12); a missing value means both are automatic. The API always returns it. */
    rewardGoals: rewardGoalsSchema.optional(),
  })
  .extend(timestampsSchema.shape);
export type Settings = z.infer<typeof settingsSchema>;

export const updateSettingsInputSchema = z
  .object({
    cycleAnchorDate: anchorDateSchema,
    vacationRanges: z.array(vacationRangeSchema),
    intervals: z.array(intervalSchema).refine(uniqueIntervalKeys, 'duplicate_interval_key'),
    aiProvider: aiProviderSettingsSchema,
    aiPrompts: aiPromptsSchema,
    aiPromptTemplates: aiPromptTemplatesSchema,
    completionControl: completionControlSchema,
    promoteThreshold: z.number().int().min(2),
    /** The amounts that apply from today on; the server writes the schedule row (administrators only). */
    periodBonuses: bonusAmountsSchema,
    /** The conversion from points to currency (administrators only, requirements 4.12). */
    currencyCode: currencyCodeSchema,
    centsPerPoint: centsPerPointSchema,
    /** The goals of the reward meter, both at once (administrators only, requirements 4.12). */
    rewardGoals: rewardGoalsSchema,
  })
  .partial();
export type UpdateSettingsInput = z.infer<typeof updateSettingsInputSchema>;

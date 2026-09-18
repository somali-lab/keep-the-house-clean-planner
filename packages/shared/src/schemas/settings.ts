import { z } from 'zod';
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
  })
  .partial();
export type UpdateSettingsInput = z.infer<typeof updateSettingsInputSchema>;

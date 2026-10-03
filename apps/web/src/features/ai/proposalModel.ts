import { format, t } from '../../i18n/nl.ts';

/** The warning shape the server returns with a proposal and in a plan diff. */
export interface ProposalWarning {
  code: string;
  placed?: unknown;
  required?: unknown;
}

/** Plain-language text for a plan warning; unknown codes fall back to the code itself. */
export function describeProposalWarning(warning: ProposalWarning): string {
  if (warning.code === 'interval_mismatch') {
    return format('ai.warning.interval', {
      placed: Number(warning.placed ?? 0),
      required: Number(warning.required ?? 0),
    });
  }
  if (warning.code === 'over_budget') return t('ai.warning.budget');
  if (warning.code === 'daily_over_budget') return t('ai.warning.dailyBudget');
  return warning.code;
}

/**
 * The "Week n:" label to put before a rationale sentence, or null when the sentence
 * already starts with its own week label (the model often writes "Week 2: ...").
 */
export function rationaleWeekLabel(line: string, weekIndex: number): string | null {
  if (/^\s*week\s*\d/i.test(line)) return null;
  return `${format('planner.week', { n: weekIndex + 1 })}:`;
}

import { Sparkles, TriangleAlert } from 'lucide-react';
import type { Ref } from 'react';
import { Button } from '@/components/ui/button';
import { t } from '../../i18n/nl.ts';
import {
  describeProposalWarning,
  rationaleWeekLabel,
  type ProposalWarning,
} from './proposalModel.ts';

/**
 * Compact explanation shown above the normal plan view when the selected plan is an AI draft:
 * it does not change the active plan, the stored rationale, and (right after creation) the warnings.
 */
export function AiDraftCard({
  rationale,
  warnings,
  cardRef,
  onManage,
}: {
  rationale: readonly string[] | null;
  warnings: readonly ProposalWarning[];
  cardRef?: Ref<HTMLElement>;
  /** Opens plan management, where the draft is activated or deleted. */
  onManage: () => void;
}) {
  return (
    <section
      ref={cardRef}
      tabIndex={-1}
      aria-labelledby="ai-draft-title"
      className="flex flex-col gap-3 rounded-2xl border border-primary/30 bg-card p-4 shadow-sm focus-visible:outline-2 focus-visible:outline-ring"
    >
      <div className="flex items-start gap-3">
        <div className="grid size-9 shrink-0 place-items-center rounded-xl bg-accent text-accent-foreground [&_svg]:size-5">
          <Sparkles aria-hidden="true" />
        </div>
        <div className="flex min-w-0 flex-col gap-1">
          <h2 id="ai-draft-title" className="text-base font-bold">
            {t('planner.aiDraft.title')}
          </h2>
          <p className="text-sm text-muted-foreground">{t('planner.aiDraft.body')}</p>
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="mt-1 w-fit"
            onClick={onManage}
          >
            {t('planner.manage')}
          </Button>
        </div>
      </div>

      {rationale && rationale.length > 0 && (
        <div className="flex flex-col gap-1">
          <h3 className="text-sm font-semibold">{t('planner.aiDraft.rationale')}</h3>
          <ol className="grid gap-1 text-sm sm:grid-cols-2">
            {rationale.map((line, index) => {
              const label = rationaleWeekLabel(line, index);
              return (
                <li key={index} className="rounded-lg bg-muted/50 px-3 py-2">
                  {label && <span className="font-semibold">{label} </span>}
                  {line}
                </li>
              );
            })}
          </ol>
        </div>
      )}

      {warnings.length > 0 && (
        <div className="flex gap-3 rounded-xl border border-warning/60 bg-warning/20 p-3 text-warning-foreground">
          <TriangleAlert className="mt-0.5 size-5 shrink-0" aria-hidden="true" />
          <div className="flex flex-col gap-1">
            <h3 className="text-sm font-bold">{t('planner.aiDraft.warnings')}</h3>
            <ul className="list-disc pl-5 text-sm">
              {warnings.map((warning, index) => (
                <li key={index}>{describeProposalWarning(warning)}</li>
              ))}
            </ul>
          </div>
        </div>
      )}
    </section>
  );
}

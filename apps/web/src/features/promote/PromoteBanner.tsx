import type { PromoteSuggestion } from '@huishoudplanner/shared';
import { Check, Sparkles, X } from 'lucide-react';
import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { ApiRequestError } from '../../api/index.ts';
import { format, t } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { weekdayName } from '../week/weekModel.ts';
import { usePromoteActions, usePromoteSuggestions } from './api.ts';

/**
 * "Je verplaatst ‘Badkamer’ steeds van dinsdag naar woensdag. Plan aanpassen?"
 * Stays hidden when there is nothing to suggest (or the request fails).
 */
export function PromoteBanner() {
  const suggestions = usePromoteSuggestions();
  const { apply, dismiss } = usePromoteActions();
  const { activeUsers } = useProfile();
  const [message, setMessage] = useState<{ kind: 'status' | 'alert'; text: string } | null>(null);

  const list = suggestions.data ?? [];
  if (list.length === 0 && !message) return null;

  const text = (s: PromoteSuggestion) => {
    const base = {
      task: s.taskName,
      from: weekdayName(s.fromSlot.weekday),
      to: weekdayName(s.toWeekday),
    };
    if (!s.toAssigneeId) return format('promote.text', base);
    const person =
      activeUsers.find((u) => u._id === s.toAssigneeId)?.name ?? t('tasks.unknownUser');
    return format('promote.textWithAssignee', { ...base, person });
  };

  const onApply = (s: PromoteSuggestion) => {
    setMessage(null);
    apply.mutate(s, {
      onSuccess: () => setMessage({ kind: 'status', text: t('promote.applied') }),
      onError: (error) =>
        setMessage({
          kind: 'alert',
          text:
            error instanceof ApiRequestError && error.status === 422
              ? t('promote.applyInvalid')
              : t('promote.error'),
        }),
    });
  };

  const onDismiss = (s: PromoteSuggestion) => {
    setMessage(null);
    dismiss.mutate(s, { onError: () => setMessage({ kind: 'alert', text: t('promote.error') }) });
  };

  const busy = apply.isPending || dismiss.isPending;

  return (
    <section
      className="promote-banner grid gap-3 rounded-2xl border border-accent-foreground/15 bg-accent p-4 text-accent-foreground shadow-sm"
      aria-label={t('promote.label')}
    >
      {list.map((s) => (
        <div
          key={`${s.taskId}-${s.fromSlot.weekIndex}-${s.fromSlot.weekday}-${s.toWeekday}`}
          className="flex gap-3 not-first:border-t not-first:border-accent-foreground/15 not-first:pt-3"
        >
          <span className="grid size-10 shrink-0 place-items-center rounded-full bg-card/70 text-accent-foreground">
            <Sparkles className="size-5" aria-hidden="true" />
          </span>
          <div className="grid min-w-0 flex-1 gap-3">
            <p className="leading-snug font-semibold">{text(s)}</p>
            <div className="flex flex-wrap gap-2">
              <Button
                type="button"
                className="h-11 rounded-full px-5"
                disabled={busy}
                onClick={() => onApply(s)}
              >
                <Check aria-hidden="true" />
                {t('promote.apply')}
              </Button>
              <Button
                type="button"
                variant="ghost"
                className="h-11 rounded-full px-4 text-accent-foreground hover:bg-card/60"
                disabled={busy}
                onClick={() => onDismiss(s)}
              >
                <X aria-hidden="true" />
                {t('promote.dismiss')}
              </Button>
            </div>
          </div>
        </div>
      ))}
      {message && (
        <p
          role={message.kind}
          className={cn(
            'rounded-xl bg-card/70 px-3 py-2 text-sm font-semibold',
            message.kind === 'alert' ? 'text-destructive' : 'text-foreground',
          )}
        >
          {message.text}
        </p>
      )}
    </section>
  );
}

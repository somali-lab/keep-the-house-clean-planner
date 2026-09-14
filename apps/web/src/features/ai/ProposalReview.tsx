import type { User } from '@huishoudplanner/shared';
import { Check, House, Sparkles, TriangleAlert, X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { format, t } from '../../i18n/nl.ts';
import { weekdayName } from '../week/weekModel.ts';
import type { DiffPosition, PlanDiffResponse } from './api.ts';

const DAYS_MONDAY_FIRST = [1, 2, 3, 4, 5, 6, 0];

type ChangeKind = 'added' | 'removed' | 'moved' | 'moved-out';

interface CellChange {
  kind: ChangeKind;
  text: string;
  roomName: string | null;
}

const ICONS: Record<ChangeKind, string> = { added: '＋', removed: '−', moved: '↔', 'moved-out': '→' };

/** Tint per change kind: the icon and the words carry the meaning, colour only supports it. */
const KIND_STYLES: Record<ChangeKind, { item: string; icon: string; text?: string }> = {
  added: { item: 'border-success/40 bg-success/10', icon: 'bg-success text-success-foreground' },
  removed: { item: 'border-destructive/30 bg-destructive/10', icon: 'bg-destructive text-destructive-foreground', text: 'line-through decoration-destructive/60' },
  moved: { item: 'border-warning/60 bg-warning/20', icon: 'bg-warning text-warning-foreground' },
  'moved-out': { item: 'border-dashed border-warning/60 bg-warning/10 text-muted-foreground', icon: 'bg-warning/60 text-warning-foreground' },
};

export interface ProposalReviewProps {
  diff: PlanDiffResponse;
  users: User[];
  rationale: string[] | null;
  busy: boolean;
  onApply(): void;
  onDiscard(): void;
}

/** A proposal shown as changes in the 4×7 grid (icons plus text), with rationale, warnings and minutes. */
export function ProposalReview({ diff, users, rationale, busy, onApply, onDiscard }: ProposalReviewProps) {
  // Used inside sentences, so "wie dan ook" stays lowercase.
  const who = (id: string | null) =>
    id === null ? t('ai.diff.anyone') : (users.find((u) => u._id === id)?.name ?? t('tasks.unknownUser'));
  const where = (p: DiffPosition) => format('ai.diff.where', { week: p.weekIndex + 1, day: weekdayName(p.weekday) });
  const at = (p: DiffPosition, weekIndex: number, weekday: number) => p.weekIndex === weekIndex && p.weekday === weekday;

  const changesFor = (weekIndex: number, weekday: number): CellChange[] => [
    ...diff.added
      .filter((s) => at(s, weekIndex, weekday))
      .map((s) => ({ kind: 'added' as const, text: format('ai.diff.added', { task: s.taskName, who: who(s.assigneeId) }), roomName: s.roomName })),
    ...diff.removed
      .filter((s) => at(s, weekIndex, weekday))
      .map((s) => ({ kind: 'removed' as const, text: format('ai.diff.removed', { task: s.taskName, who: who(s.assigneeId) }), roomName: s.roomName })),
    ...diff.moved
      .filter((m) => at(m.to, weekIndex, weekday))
      .map((m) => ({
        kind: 'moved' as const,
        text:
          at(m.from, m.to.weekIndex, m.to.weekday)
            ? format('ai.diff.reassigned', { task: m.taskName, from: who(m.from.assigneeId), to: who(m.to.assigneeId) })
            : format('ai.diff.movedIn', { task: m.taskName, from: where(m.from), who: who(m.to.assigneeId) }),
        roomName: m.roomName,
      })),
    ...diff.moved
      .filter((m) => at(m.from, weekIndex, weekday) && !at(m.to, weekIndex, weekday))
      .map((m) => ({ kind: 'moved-out' as const, text: format('ai.diff.movedOut', { task: m.taskName, to: where(m.to) }), roomName: m.roomName })),
  ];

  const nothingChanged = diff.added.length + diff.removed.length + diff.moved.length === 0;

  return (
    <section
      className="flex flex-col gap-6 rounded-2xl border-2 border-primary/25 bg-card p-6 pb-0 text-card-foreground shadow-md"
      aria-labelledby="proposal-title"
    >
      <div className="flex items-start gap-3">
        <div className="grid size-10 shrink-0 place-items-center rounded-xl bg-primary/10 text-primary">
          <Sparkles className="size-5" aria-hidden="true" />
        </div>
        <div className="min-w-0">
          <h2 id="proposal-title" className="text-lg leading-tight font-bold">
            {t('ai.review.title')}
          </h2>
          <p className="mt-1 text-sm text-muted-foreground">
            {format('ai.review.counts', {
              added: diff.added.length,
              removed: diff.removed.length,
              moved: diff.moved.length,
              unchanged: diff.unchanged,
            })}
          </p>
        </div>
      </div>

      {nothingChanged ? (
        <p className="rounded-xl border border-dashed px-4 py-6 text-center text-muted-foreground">{t('ai.review.noChanges')}</p>
      ) : (
        <div className="flex flex-col gap-3">
          <p className="text-sm font-semibold text-muted-foreground">{t('ai.review.gridCaption')}</p>
          <div className="grid items-start gap-4 lg:grid-cols-2">
            {[0, 1, 2, 3].map((w) => {
              const changedDays = DAYS_MONDAY_FIRST.map((day) => ({ day, changes: changesFor(w, day) })).filter(
                ({ changes }) => changes.length > 0,
              );
              return (
                <section key={w} className="overflow-hidden rounded-xl border bg-background/40">
                  <h3 className="bg-secondary/60 px-4 py-2.5 text-sm font-bold">{format('planner.week', { n: w + 1 })}</h3>
                  {changedDays.length === 0 ? (
                    <p className="px-4 py-4 text-sm text-muted-foreground">{t('ai.review.noChanges')}</p>
                  ) : (
                    <div className="divide-y">
                      {changedDays.map(({ day, changes }) => (
                        <div key={day} data-testid={`diff-${w}-${day}`} className="grid gap-2 p-3 sm:grid-cols-[6.5rem_minmax(0,1fr)]">
                          <h4 className="text-sm font-bold capitalize">{weekdayName(day)}</h4>
                          <ul className="flex min-w-0 flex-col gap-2">
                            {changes.map((change, index) => (
                              <li
                                key={index}
                                className={cn('flex items-start gap-2 rounded-lg border px-2.5 py-2 text-sm leading-snug', KIND_STYLES[change.kind].item)}
                              >
                                <span
                                  aria-hidden="true"
                                  className={cn(
                                    'mt-0.5 grid size-5 shrink-0 place-items-center rounded-full text-xs leading-none font-bold',
                                    KIND_STYLES[change.kind].icon,
                                  )}
                                >
                                  {ICONS[change.kind]}{' '}
                                </span>
                                <span className="min-w-0">
                                  <span className={cn('block', KIND_STYLES[change.kind].text)}>{change.text}</span>
                                  <span className="mt-1 flex items-center gap-1 text-xs font-medium text-muted-foreground">
                                    <House className="size-3.5 shrink-0" aria-hidden="true" />
                                    {change.roomName ?? t('tasks.unknownRoom')}
                                  </span>
                                </span>
                              </li>
                            ))}
                          </ul>
                        </div>
                      ))}
                    </div>
                  )}
                </section>
              );
            })}
          </div>
        </div>
      )}

      {rationale && (
        <div className="flex flex-col gap-3">
          <h3 className="text-base font-bold">{t('ai.review.rationale')}</h3>
          <ol className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            {rationale.map((line, i) => (
              <li key={i} className="rounded-xl border bg-secondary/40 px-4 py-3 text-sm">
                {line}
              </li>
            ))}
          </ol>
        </div>
      )}

      {diff.warnings.length > 0 && (
        <div className="flex gap-3 rounded-xl border border-warning/60 bg-warning/20 p-4 text-warning-foreground">
          <TriangleAlert className="mt-0.5 size-5 shrink-0" aria-hidden="true" />
          <div className="flex flex-col gap-1">
            <h3 className="text-base font-bold">{t('ai.review.warnings')}</h3>
            <ul className="list-disc pl-5 text-sm">
              {diff.warnings.map((w, i) => (
                <li key={i}>
                  {w.code === 'interval_mismatch'
                    ? format('ai.warning.interval', { placed: w.placed ?? 0, required: w.required ?? 0 })
                    : w.code === 'over_budget'
                      ? t('ai.warning.budget')
                      : w.code === 'daily_over_budget'
                        ? t('ai.warning.dailyBudget')
                      : w.code}
                </li>
              ))}
            </ul>
          </div>
        </div>
      )}

      <div className="flex flex-col gap-3">
        <h3 className="text-base font-bold">{t('ai.review.minutes')}</h3>
        <div className="overflow-x-auto rounded-xl border">
          <table className="w-full border-collapse text-sm">
            <thead>
              <tr className="bg-secondary/40">
                <th scope="col" className="px-3 py-2 text-left text-xs font-bold text-muted-foreground uppercase">
                  {t('ai.review.person')}
                </th>
                {[0, 1, 2, 3].map((w) => (
                  <th key={w} scope="col" className="px-3 py-2 text-left text-xs font-bold text-muted-foreground uppercase">
                    {format('planner.week', { n: w + 1 })}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {users.map((user) => (
                <tr key={user._id} className="border-t">
                  <th scope="row" className="px-3 py-2 text-left font-semibold">
                    <span className="inline-flex items-center gap-2">
                      <span className="size-3 rounded-full" style={{ background: user.color }} aria-hidden="true" />
                      {user.name}
                    </span>
                  </th>
                  {[0, 1, 2, 3].map((w) => {
                    const before = diff.summary.before[w]?.users.find((u) => u.userId === user._id)?.minutes ?? 0;
                    const after = diff.summary.after[w]?.users.find((u) => u.userId === user._id)?.minutes ?? 0;
                    return (
                      <td key={w} className={cn('px-3 py-2 tabular-nums', before !== after ? 'font-bold text-primary' : 'text-muted-foreground')}>
                        {format('ai.review.beforeAfter', { before, after })}
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <div className="sticky bottom-0 -mx-6 flex justify-end gap-2 rounded-b-2xl border-t bg-card/95 px-6 py-4 backdrop-blur">
        <Button
          type="button"
          variant="outline"
          className="border-destructive/40 text-destructive hover:bg-destructive/10 hover:text-destructive"
          disabled={busy}
          onClick={onDiscard}
        >
          <X aria-hidden="true" />
          {t('ai.discard')}
        </Button>
        <Button type="button" disabled={busy} onClick={onApply}>
          <Check aria-hidden="true" />
          {t('ai.apply')}
        </Button>
      </div>
    </section>
  );
}

import type { OccurrenceView, User } from '@huishoudplanner/shared';
import {
  ArrowRight,
  Check,
  CheckCircle2,
  Circle,
  Hand,
  MoreHorizontal,
  SkipForward,
  TriangleAlert,
  ThumbsUp,
  Undo2,
} from 'lucide-react';
import { useId, useState } from 'react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { cn } from '@/lib/utils';
import { format, t } from '../../i18n/nl.ts';

export interface OccurrenceItemProps {
  occurrence: OccurrenceView;
  roomName: string | undefined;
  users: User[];
  completionControl?: 'circle' | 'thumb';
  onComplete(): void;
  onUncomplete(): void;
  onSkip(reason: string): void;
  onClaim(): void;
}

/** "wo 16-09" style date for day keys. */
export function shortDate(dayKey: string): string {
  const [y, m, d] = dayKey.split('-').map(Number) as [number, number, number];
  const weekday = new Date(Date.UTC(y, m - 1, d)).getUTCDay();
  return `${t(`weekday.${weekday}` as 'weekday.0')} ${String(d).padStart(2, '0')}-${String(m).padStart(2, '0')}`;
}

export function OccurrenceItem({
  occurrence: occ,
  roomName,
  users,
  completionControl = 'circle',
  onComplete,
  onUncomplete,
  onSkip,
  onClaim,
}: OccurrenceItemProps) {
  const idPrefix = useId();
  const [menuOpen, setMenuOpen] = useState(false);
  const [skipping, setSkipping] = useState(false);
  const [reason, setReason] = useState('');
  const task = occ.taskNameSnapshot;
  const userName = (id: string | null) =>
    id === null
      ? t('today.anyone')
      : (users.find((u) => u._id === id)?.name ?? t('tasks.unknownUser'));
  const isOpen = occ.status === 'open';

  return (
    <li
      className={cn(
        'rounded-2xl border bg-card p-3 shadow-sm transition-colors',
        !isOpen && 'bg-card/60 shadow-none',
        occ.isOverdue && 'border-l-4 border-l-warning',
      )}
    >
      <div className="flex items-center gap-3">
        {isOpen ? (
          <button
            type="button"
            className="check-button grid size-14 shrink-0 cursor-pointer place-items-center rounded-full border-2 border-muted-foreground/40 bg-background text-muted-foreground transition-all outline-none hover:border-success hover:bg-success/10 hover:text-success focus-visible:ring-[3px] focus-visible:ring-ring/50 active:scale-95"
            aria-label={format('today.completeNamed', { task })}
            onClick={onComplete}
          >
            {completionControl === 'thumb' ? (
              <ThumbsUp className="size-7" aria-hidden="true" />
            ) : (
              <Circle className="size-7" aria-hidden="true" />
            )}
          </button>
        ) : (
          <span
            className={cn(
              'grid size-14 shrink-0 place-items-center rounded-full',
              occ.status === 'done'
                ? 'bg-success text-success-foreground'
                : 'bg-muted text-muted-foreground',
            )}
            aria-hidden="true"
          >
            {occ.status === 'done' ? (
              <Check className="size-7" strokeWidth={3} />
            ) : (
              <SkipForward className="size-6" />
            )}
          </span>
        )}

        <div className={cn('grid min-w-0 flex-1 gap-1', !isOpen && 'opacity-75')}>
          <strong
            className={cn(
              'leading-snug font-bold break-words',
              !isOpen && 'font-semibold text-muted-foreground line-through',
            )}
          >
            {task}
          </strong>
          <span className="text-sm text-muted-foreground">
            {[
              roomName,
              format('tasks.minutes', { minutes: occ.durationMinutesSnapshot }),
              userName(occ.assigneeId),
            ]
              .filter(Boolean)
              .join(' · ')}
          </span>
          {occ.isOverdue && (
            <Badge className="bg-warning whitespace-normal text-warning-foreground">
              <TriangleAlert aria-hidden="true" />
              {format('today.overdueSince', { date: shortDate(occ.date) })}
            </Badge>
          )}
          {occ.movedFrom && (
            <span className="flex items-center gap-1 text-xs text-muted-foreground">
              <ArrowRight className="size-3 shrink-0" aria-hidden="true" />
              {format('today.movedFrom', { date: shortDate(occ.movedFrom) })}
            </span>
          )}
          {occ.status === 'done' && (
            <span className="flex items-center gap-1 text-xs font-semibold text-success">
              <CheckCircle2 className="size-3.5 shrink-0" aria-hidden="true" />
              {format('today.doneBy', { name: userName(occ.completedBy) })}
            </span>
          )}
          {occ.status === 'skipped' && (
            <span className="flex items-center gap-1 text-xs text-muted-foreground">
              <SkipForward className="size-3.5 shrink-0" aria-hidden="true" />
              {occ.skipReason
                ? format('today.skippedWithReason', { reason: occ.skipReason })
                : t('today.skipped')}
            </span>
          )}
        </div>

        <div className="flex shrink-0 items-center gap-1">
          {isOpen && occ.assigneeId === null && (
            <Button
              type="button"
              variant="secondary"
              className="h-11 rounded-full px-4"
              onClick={onClaim}
              aria-label={format('today.claimNamed', { task })}
            >
              <Hand aria-hidden="true" />
              {t('today.claim')}
            </Button>
          )}
          {occ.status === 'done' && (
            <Button
              type="button"
              variant="ghost"
              className="h-11 rounded-full px-3 text-muted-foreground"
              onClick={onUncomplete}
              aria-label={format('today.undoNamed', { task })}
            >
              <Undo2 aria-hidden="true" />
              <span className="sr-only sm:not-sr-only">{t('today.undo')}</span>
            </Button>
          )}
          {isOpen && (
            <Button
              type="button"
              variant="ghost"
              className={cn(
                'size-11 rounded-full text-muted-foreground',
                menuOpen && 'bg-secondary text-foreground',
              )}
              aria-expanded={menuOpen}
              aria-controls={`${idPrefix}-menu`}
              aria-label={format('today.moreNamed', { task })}
              onClick={() => setMenuOpen((open) => !open)}
            >
              <MoreHorizontal className="size-5" aria-hidden="true" />
              <span className="sr-only">{t('today.more')}</span>
            </Button>
          )}
        </div>
      </div>

      {isOpen && menuOpen && (
        <div id={`${idPrefix}-menu`} className="mt-3 grid gap-3 rounded-xl bg-secondary/60 p-3">
          {skipping ? (
            <div className="grid gap-1.5">
              <Label htmlFor={`${idPrefix}-reason`}>{t('today.skipReason')}</Label>
              <div className="flex flex-wrap gap-2">
                <Input
                  id={`${idPrefix}-reason`}
                  className="h-11 min-w-40 flex-1 bg-card"
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                />
                <Button
                  type="button"
                  variant="outline"
                  className="h-11 rounded-full px-4"
                  onClick={() => onSkip(reason.trim())}
                >
                  <SkipForward aria-hidden="true" />
                  {t('today.skipConfirm')}
                </Button>
              </div>
            </div>
          ) : (
            <Button
              type="button"
              variant="outline"
              className="h-11 w-full rounded-full"
              onClick={() => setSkipping(true)}
            >
              <SkipForward aria-hidden="true" />
              {t('today.skip')}
            </Button>
          )}
        </div>
      )}
    </li>
  );
}

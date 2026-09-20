import type { User } from '@huishoudplanner/shared';
import { CalendarDays, CheckCircle2, Circle, Clock, ThumbsUp, TriangleAlert } from 'lucide-react';
import { useId, useState, type FormEvent } from 'react';
import { EmptyState } from '@/components/EmptyState';
import { NativeSelect } from '@/components/NativeSelect';
import { PageHeader } from '@/components/PageHeader';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { cn } from '@/lib/utils';
import { useSettings } from '../../api/queries.ts';
import { format, t } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { dayKeyInZone } from '../today/todayModel.ts';
import { useDue, useDueActions, type DueItemView } from './api.ts';

const MONTHS = ['jan', 'feb', 'mrt', 'apr', 'mei', 'jun', 'jul', 'aug', 'sep', 'okt', 'nov', 'dec'];
const WEEKDAYS = ['zo', 'ma', 'di', 'wo', 'do', 'vr', 'za'];

/** "za 12 sep" */
export function spokenDate(dayKey: string): string {
  const [y, m, d] = dayKey.split('-').map(Number) as [number, number, number];
  const weekday = new Date(Date.UTC(y, m - 1, d)).getUTCDay();
  return `${WEEKDAYS[weekday]} ${d} ${MONTHS[m - 1]}`;
}

export function DuePage({ now }: { now?: Date }) {
  const settings = useSettings();
  const due = useDue();
  const { profile, activeUsers } = useProfile();
  const { plan, doneNow } = useDueActions();
  const [planning, setPlanning] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  if (settings.isPending || due.isPending)
    return (
      <p role="status" className="py-10 text-center text-muted-foreground">
        {t('app.loading')}
      </p>
    );
  if (settings.isError || due.isError)
    return (
      <p role="alert" className="rounded-2xl bg-destructive/10 p-4 text-destructive">
        {t('app.error')}
      </p>
    );

  const todayKey = dayKeyInZone(now ?? new Date(), settings.data.timezone);
  const items = due.data.filter((item) => item.state !== 'ok');
  const onError = () => setFailed(true);

  return (
    <section className="flex flex-col gap-5">
      <PageHeader title={t('nav.due')} description={t('due.explainer')} className="mb-0" />
      {failed && (
        <p
          role="alert"
          className="flex items-center gap-2 rounded-2xl bg-destructive/10 p-4 font-semibold text-destructive"
        >
          <TriangleAlert className="size-5 shrink-0" aria-hidden="true" />
          {t('due.actionError')}
        </p>
      )}

      {items.length === 0 ? (
        <EmptyState icon={<CheckCircle2 className="size-6" aria-hidden="true" />}>
          {t('due.empty')}
        </EmptyState>
      ) : (
        <ol className="grid gap-3">
          {items.map((item, index) => (
            <DueRow
              key={item.taskId}
              rank={index + 1}
              item={item}
              users={activeUsers}
              completionControl={settings.data.completionControl ?? 'circle'}
              todayKey={todayKey}
              planning={planning === item.taskId}
              busy={plan.isPending || doneNow.isPending}
              onOpenPlan={() => setPlanning(item.taskId)}
              onCancelPlan={() => setPlanning(null)}
              onPlan={(date, assigneeId) => {
                setFailed(false);
                plan.mutate(
                  { taskId: item.taskId, date, assigneeId },
                  { onSuccess: () => setPlanning(null), onError },
                );
              }}
              onDoneNow={() => {
                setFailed(false);
                doneNow.mutate({ item, todayKey, profileId: profile?._id ?? '' }, { onError });
              }}
            />
          ))}
        </ol>
      )}
    </section>
  );
}

interface DueRowProps {
  rank: number;
  item: DueItemView;
  users: User[];
  completionControl: 'circle' | 'thumb';
  todayKey: string;
  planning: boolean;
  busy: boolean;
  onOpenPlan(): void;
  onCancelPlan(): void;
  onPlan(date: string, assigneeId: string | null): void;
  onDoneNow(): void;
}

function DueRow({
  rank,
  item,
  users,
  completionControl,
  todayKey,
  planning,
  busy,
  onOpenPlan,
  onCancelPlan,
  onPlan,
  onDoneNow,
}: DueRowProps) {
  const idPrefix = useId();
  const [date, setDate] = useState(todayKey);
  const [assignee, setAssignee] = useState('');
  const task = item.taskName;
  const overdue = item.state === 'overdue';
  // Bar is full at twice the interval; "due" starts at 1×, "overdue" at 1.5×.
  const fill = Math.min(Math.max(item.ratio / 2, 0.05), 1);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    onPlan(date, assignee || null);
  };

  return (
    <li
      className={cn(
        'due-item grid gap-3 rounded-2xl border bg-card p-4 shadow-sm',
        overdue && 'is-overdue border-2 border-warning',
      )}
    >
      <div className="flex items-start gap-3">
        <span
          className={cn(
            'grid size-9 shrink-0 place-items-center rounded-full text-sm font-extrabold',
            overdue
              ? 'bg-warning text-warning-foreground'
              : 'bg-secondary text-secondary-foreground',
          )}
          aria-hidden="true"
        >
          {rank}
        </span>
        <div className="grid min-w-0 flex-1 gap-1.5">
          <div className="flex flex-wrap items-center gap-2">
            <strong className="leading-snug font-bold break-words">{task}</strong>
            {overdue ? (
              <Badge className="bg-warning text-warning-foreground">
                <span aria-hidden="true">⚠</span> {t('due.overdue')}
              </Badge>
            ) : (
              <Badge variant="secondary">
                <Clock aria-hidden="true" />
                {t('due.due')}
              </Badge>
            )}
          </div>
          <span className="text-sm text-muted-foreground">
            {[
              item.roomName,
              item.intervalLabel,
              item.lastCompletedAt
                ? item.daysSince === 1
                  ? t('due.daysSinceOne')
                  : format('due.daysSince', { days: item.daysSince })
                : format('due.initialDue', { date: spokenDate(item.initialDueDate) }),
            ]
              .filter(Boolean)
              .join(' · ')}
          </span>
          <div className="h-2 overflow-hidden rounded-full bg-secondary" aria-hidden="true">
            <div
              className={cn('h-full rounded-full', overdue ? 'bg-warning' : 'bg-primary/70')}
              style={{ width: `${fill * 100}%` }}
            />
          </div>
          {item.nextOccurrence && (
            <p className="flex items-start gap-1.5 text-sm text-muted-foreground italic">
              <CalendarDays className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
              <span>
                {format(item.lastCompletedAt ? 'due.plannedNote' : 'due.plannedNoteNever', {
                  date: spokenDate(item.nextOccurrence.date),
                })}
              </span>
            </p>
          )}
        </div>
      </div>

      <div className="grid grid-cols-2 gap-2">
        <Button
          type="button"
          variant="outline"
          className="h-11 rounded-full"
          onClick={onOpenPlan}
          disabled={busy}
          aria-label={format('due.planNamed', { task })}
        >
          <CalendarDays aria-hidden="true" />
          {t('due.plan')}
        </Button>
        <Button
          type="button"
          className="h-11 rounded-full"
          onClick={onDoneNow}
          disabled={busy}
          aria-label={format('due.doneNowNamed', { task })}
        >
          {completionControl === 'thumb' ? <ThumbsUp aria-hidden="true" /> : <Circle aria-hidden="true" />}
          {t('due.doneNow')}
        </Button>
      </div>

      {planning && (
        <form
          className="grid gap-3 rounded-xl bg-secondary/60 p-3"
          onSubmit={submit}
          aria-label={format('due.planNamed', { task })}
        >
          <div className="grid gap-1.5">
            <Label htmlFor={`${idPrefix}-date`}>{t('due.date')}</Label>
            <Input
              id={`${idPrefix}-date`}
              type="date"
              className="h-11 bg-card"
              value={date}
              min={todayKey}
              onChange={(e) => setDate(e.target.value)}
              required
            />
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor={`${idPrefix}-who`}>{t('due.assignee')}</Label>
            <NativeSelect
              id={`${idPrefix}-who`}
              className="[&_select]:h-11"
              value={assignee}
              onChange={(e) => setAssignee(e.target.value)}
            >
              <option value="">{t('today.anyone')}</option>
              {users.map((user) => (
                <option key={user._id} value={user._id}>
                  {user.name}
                </option>
              ))}
            </NativeSelect>
          </div>
          <div className="grid grid-cols-2 gap-2">
            <Button type="submit" className="h-11 rounded-full" disabled={busy}>
              {t('due.planConfirm')}
            </Button>
            <Button
              type="button"
              variant="ghost"
              className="h-11 rounded-full"
              onClick={onCancelPlan}
            >
              {t('common.cancel')}
            </Button>
          </div>
        </form>
      )}
    </li>
  );
}

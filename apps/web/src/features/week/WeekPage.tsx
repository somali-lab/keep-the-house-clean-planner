import {
  DndContext,
  KeyboardSensor,
  PointerSensor,
  TouchSensor,
  useDraggable,
  useDroppable,
  useSensor,
  useSensors,
  type DragEndEvent,
} from '@dnd-kit/core';
import type { ApiWarning, OccurrenceView, User } from '@huishoudplanner/shared';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import {
  CalendarDays,
  Check,
  CheckCircle2,
  Circle,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  ChevronUp,
  GripVertical,
  SkipForward,
  TriangleAlert,
  ThumbsUp,
} from 'lucide-react';
import { useMemo, useState } from 'react';
import { PageHeader } from '@/components/PageHeader';
import { NativeSelect } from '@/components/NativeSelect';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { api } from '../../api/index.ts';
import { useRooms, useSettings, useTasks } from '../../api/queries.ts';
import { format, t } from '../../i18n/nl.ts';
import { Avatar } from '../../identity/Avatar.tsx';
import { useProfile } from '../../identity/index.ts';
import { PromoteBanner } from '../promote/PromoteBanner.tsx';
import { occurrenceKeys, useOccurrenceAction, useOccurrences } from '../today/api.ts';
import { addDaysKey, dayKeyInZone } from '../today/todayModel.ts';
import {
  dayDropId,
  groupByDay,
  longDay,
  movedTo,
  occurrenceDragId,
  overviewDays,
  parseDayDropId,
  parseOccurrenceDragId,
  shortDay,
  weekRangeLabel,
  weekdayName,
} from './weekModel.ts';

export function warningText(warning: ApiWarning, users: User[]): string {
  if (warning.code === 'assignee_unavailable') {
    const userId = typeof warning.details?.userId === 'string' ? warning.details.userId : '';
    const weekday = typeof warning.details?.weekday === 'number' ? warning.details.weekday : -1;
    return format('week.warning.unavailable', {
      user: users.find((u) => u._id === userId)?.name ?? t('tasks.unknownUser'),
      weekday: weekdayName(weekday),
    });
  }
  return warning.message;
}

/**
 * The week as it really is (occurrences, not the template). Items can be
 * dragged to another day (touch: hold 200 ms) or moved via "Verplaats naar…".
 */
export function WeekPage({ now }: { now?: Date }) {
  const settings = useSettings();
  const tasks = useTasks();
  const rooms = useRooms();
  const { profile, activeUsers } = useProfile();
  const queryClient = useQueryClient();
  const todayKey = dayKeyInZone(now ?? new Date(), settings.data?.timezone ?? 'Europe/Amsterdam');
  const [periodOffset, setPeriodOffset] = useState(0);
  const [pastExpanded, setPastExpanded] = useState(false);
  const [personFilter, setPersonFilter] = useState('all');
  const days = overviewDays(addDaysKey(todayKey, periodOffset * 7));
  const visibleDays = pastExpanded ? days : days.slice(3);
  const from = days[0]!;
  const to = days.at(-1)!;
  const queryKey = occurrenceKeys.range(from, to);
  const occurrences = useOccurrences(from, to, settings.isSuccess);
  const occurrenceAction = useOccurrenceAction(queryKey, { profileId: profile?._id ?? '', todayKey });
  const [warnings, setWarnings] = useState<ApiWarning[]>([]);
  const [failed, setFailed] = useState(false);
  const roomByTask = useMemo(() => {
    const roomNames = new Map((rooms.data ?? []).map((room) => [room._id, room.name]));
    return new Map((tasks.data ?? []).map((task) => [task._id, roomNames.get(task.roomId) ?? t('tasks.unknownRoom')]));
  }, [rooms.data, tasks.data]);

  const move = useMutation({
    mutationFn: async ({ id, date }: { id: string; date: string }) =>
      api.patch<OccurrenceView>(`/api/occurrences/${id}`, { action: 'reschedule', date }),
    onMutate: async ({ id, date }) => {
      setFailed(false);
      setWarnings([]);
      await queryClient.cancelQueries({ queryKey });
      const previous = queryClient.getQueryData<OccurrenceView[]>(queryKey);
      queryClient.setQueryData<OccurrenceView[]>(queryKey, (list) =>
        list?.map((occ) => (occ._id === id ? movedTo(occ, date) : occ)),
      );
      return { previous };
    },
    onError: (_error, _vars, context) => {
      if (context?.previous) queryClient.setQueryData(queryKey, context.previous);
      setFailed(true);
    },
    onSuccess: (result) => setWarnings(result.warnings),
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: ['occurrences'] }),
        queryClient.invalidateQueries({ queryKey: ['due'] }),
      ]),
  });

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
    useSensor(TouchSensor, { activationConstraint: { delay: 200, tolerance: 5 } }),
    useSensor(KeyboardSensor),
  );

  const requestMove = (id: string, date: string) => {
    const occ = occurrences.data?.find((o) => o._id === id);
    if (!occ || occ.date === date) return;
    move.mutate({ id, date });
  };

  const onDragEnd = ({ active, over }: DragEndEvent) => {
    if (!over) return;
    const id = parseOccurrenceDragId(String(active.id));
    const date = parseDayDropId(String(over.id));
    if (id && date) requestMove(id, date);
  };

  if (settings.isPending || tasks.isPending || rooms.isPending || occurrences.isPending)
    return (
      <p role="status" className="py-10 text-center text-muted-foreground">
        {t('app.loading')}
      </p>
    );
  if (settings.isError || tasks.isError || rooms.isError || occurrences.isError)
    return (
      <p role="alert" className="rounded-2xl bg-destructive/10 p-4 text-destructive">
        {t('app.error')}
      </p>
    );

  const filteredOccurrences = occurrences.data.filter((occurrence) =>
    personFilter === 'all'
      ? true
      : personFilter === 'unassigned'
        ? occurrence.assigneeId === null
        : occurrence.assigneeId === personFilter,
  );
  const openCount = filteredOccurrences.filter((occurrence) => occurrence.status === 'open').length;
  const finishedCount = filteredOccurrences.length - openCount;

  return (
    <section className="flex flex-col gap-5">
      <PageHeader
        title={t('week.overviewTitle')}
        description={weekRangeLabel(from, to)}
        className="mb-0"
        actions={
          <div
            className="inline-flex items-center gap-1 rounded-full border bg-card p-1 shadow-sm"
            role="group"
            aria-label={t('week.navigation')}
          >
            <Button
              type="button"
              variant="ghost"
              size="icon"
              className="rounded-full"
              aria-label={t('week.previous')}
              onClick={() => setPeriodOffset((offset) => offset - 1)}
            >
              <ChevronLeft aria-hidden="true" />
            </Button>
            <Button
              type="button"
              variant={periodOffset === 0 ? 'default' : 'ghost'}
              className="h-10 rounded-full px-4"
              onClick={() => setPeriodOffset(0)}
            >
              {t('week.aroundToday')}
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="icon"
              className="rounded-full"
              aria-label={t('week.next')}
              onClick={() => setPeriodOffset((offset) => offset + 1)}
            >
              <ChevronRight aria-hidden="true" />
            </Button>
          </div>
        }
      />
      <div className="flex flex-wrap items-center gap-2 rounded-2xl border bg-card px-4 py-3 shadow-sm">
        <span className="flex items-center gap-2 font-extrabold">
          <CalendarDays className="size-5 text-primary" aria-hidden="true" />
          {format('week.total', { count: filteredOccurrences.length })}
        </span>
        <NativeSelect
          className="ml-auto w-48"
          aria-label={t('week.filterPerson')}
          value={personFilter}
          onChange={(event) => setPersonFilter(event.target.value)}
        >
          <option value="all">{t('week.allPeople')}</option>
          {activeUsers.map((user) => <option key={user._id} value={user._id}>{user.name}</option>)}
          <option value="unassigned">{t('planner.anyone')}</option>
        </NativeSelect>
        <Badge className="rounded-full px-3 py-1">
          {format('week.open', { count: openCount })}
        </Badge>
        <Badge variant="secondary" className="rounded-full px-3 py-1">
          <CheckCircle2 aria-hidden="true" />
          {format('week.finished', { count: finishedCount })}
        </Badge>
      </div>
      <PromoteBanner />

      {failed && (
        <p
          role="alert"
          className="flex items-center gap-2 rounded-2xl bg-destructive/10 p-4 font-semibold text-destructive"
        >
          <TriangleAlert className="size-5 shrink-0" aria-hidden="true" />
          {t('week.moveError')}
        </p>
      )}
      {warnings.length > 0 && (
        <div
          role="status"
          className="grid gap-1 rounded-2xl border border-warning bg-warning/25 p-4 text-sm font-semibold"
        >
          {warnings.map((w, i) => (
            <p key={i} className="flex items-start gap-2">
              <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
              <span>{warningText(w, activeUsers)}</span>
            </p>
          ))}
        </div>
      )}

      <DndContext sensors={sensors} onDragEnd={onDragEnd}>
        <button
          type="button"
          className="mb-4 flex w-full items-center gap-3 rounded-2xl border bg-card px-4 py-3 text-left shadow-sm transition-colors hover:bg-accent/40 focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none"
          aria-expanded={pastExpanded}
          onClick={() => setPastExpanded((expanded) => !expanded)}
        >
          <span className="grid size-9 shrink-0 place-items-center rounded-full bg-secondary text-secondary-foreground">
            {pastExpanded ? <ChevronUp aria-hidden="true" /> : <ChevronDown aria-hidden="true" />}
          </span>
          <span className="min-w-0 flex-1">
            <strong className="block">{t('week.pastDays')}</strong>
            <span className="text-sm text-muted-foreground">{weekRangeLabel(days[0]!, days[2]!)}</span>
          </span>
          <Badge variant="secondary" className="rounded-full">
            3
          </Badge>
        </button>
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {groupByDay(filteredOccurrences, visibleDays).map((day) => (
            <DayColumn
              key={day.dayKey}
              dayKey={day.dayKey}
              isToday={day.dayKey === todayKey}
              period={day.dayKey < todayKey ? 'past' : day.dayKey === todayKey ? 'today' : 'future'}
              items={day.items}
              users={activeUsers}
              roomByTask={roomByTask}
              completionControl={settings.data.completionControl ?? 'circle'}
              onComplete={(id) => occurrenceAction.mutate({ id, kind: 'complete' }, { onError: () => setFailed(true) })}
              onUncomplete={(id) => occurrenceAction.mutate({ id, kind: 'uncomplete' }, { onError: () => setFailed(true) })}
            />
          ))}
        </div>
      </DndContext>
    </section>
  );
}

interface DayColumnProps {
  dayKey: string;
  isToday: boolean;
  period: 'past' | 'today' | 'future';
  items: OccurrenceView[];
  users: User[];
  roomByTask: Map<string, string>;
  completionControl: 'circle' | 'thumb';
  onComplete(id: string): void;
  onUncomplete(id: string): void;
}

function DayColumn({ dayKey, isToday, period, items, users, roomByTask, completionControl, onComplete, onUncomplete }: DayColumnProps) {
  const { setNodeRef, isOver } = useDroppable({ id: dayDropId(dayKey) });
  const headingId = `day-${dayKey}`;
  return (
    <section
      ref={setNodeRef}
      className={cn(
        'min-w-0 rounded-2xl border-2 bg-card p-4 shadow-sm transition-all',
        period === 'past' && 'border-muted-foreground/25 bg-muted/20',
        period === 'today' && 'border-primary bg-primary/[0.03] dark:border-primary/55',
        period === 'future' && 'border-success/45 bg-success/[0.03] dark:border-success/25 dark:bg-success/[0.025]',
        isOver && 'bg-primary/5 ring-2 ring-primary ring-offset-2 ring-offset-background',
      )}
      aria-labelledby={headingId}
      data-testid={dayDropId(dayKey)}
      data-period={period}
    >
      <div className="mb-3 flex min-h-11 items-start justify-between gap-2">
        <h2 id={headingId} className="flex flex-wrap items-center gap-1.5 text-sm font-extrabold leading-tight">
          {longDay(dayKey)}
          {isToday && (
            <>
              {' '}
              <Badge>{t('nav.today')}</Badge>
            </>
          )}
        </h2>
        {items.length > 0 && (
          <Badge variant="secondary" className="min-w-6 rounded-full">
            {items.length}
          </Badge>
        )}
      </div>
      {items.length === 0 ? (
        <p className="rounded-xl border border-dashed px-3 py-4 text-center text-sm text-muted-foreground">
          {t('week.emptyDay')}
        </p>
      ) : (
        <ul className="grid gap-2">
          {items.map((occ) => (
            <WeekItem
              key={occ._id}
              occ={occ}
              users={users}
              roomName={occ.roomNameSnapshot ?? roomByTask.get(occ.taskId) ?? t('tasks.unknownRoom')}
              completionControl={completionControl}
              onComplete={onComplete}
              onUncomplete={onUncomplete}
            />
          ))}
        </ul>
      )}
    </section>
  );
}

function WeekItem({
  occ,
  users,
  roomName,
  completionControl,
  onComplete,
  onUncomplete,
}: {
  occ: OccurrenceView;
  users: User[];
  roomName: string;
  completionControl: 'circle' | 'thumb';
  onComplete(id: string): void;
  onUncomplete(id: string): void;
}) {
  const isOpen = occ.status === 'open';
  const { attributes, listeners, setNodeRef, setActivatorNodeRef, transform, isDragging } =
    useDraggable({
      id: occurrenceDragId(occ._id),
      disabled: !isOpen,
    });
  const style = transform
    ? { transform: `translate3d(${transform.x}px, ${transform.y}px, 0)` }
    : undefined;
  const assigneeUser = users.find((u) => u._id === occ.assigneeId);
  const assignee = occ.assigneeId === null ? t('today.anyone') : (assigneeUser?.name ?? t('tasks.unknownUser'));
  const task = occ.taskNameSnapshot;

  return (
    <li
      ref={setNodeRef}
      style={style}
      className={cn(
        'min-w-0 touch-none overflow-hidden rounded-xl border bg-background transition-shadow',
        !isOpen && 'opacity-60 shadow-none',
        occ.isOverdue && 'border-l-4 border-l-warning',
        isDragging && 'relative z-20 opacity-80 shadow-lg ring-2 ring-primary',
      )}
    >
      <div className="grid grid-cols-[minmax(0,1fr)_2.25rem_2.25rem] items-center gap-1.5 p-2">
        <span
          ref={setActivatorNodeRef}
          className={cn(
            'drag-handle flex min-h-11 min-w-0 flex-1 touch-none items-start gap-2 rounded-lg px-1.5 py-1 outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50',
            isOpen ? 'cursor-grab active:cursor-grabbing' : 'cursor-default',
          )}
          {...listeners}
          {...attributes}
        >
          <span className="flex shrink-0 flex-col items-center gap-1.5">
            {isOpen ? (
              <GripVertical className="size-4 text-muted-foreground" aria-hidden="true" />
            ) : occ.status === 'done' ? (
              <CheckCircle2 className="size-4 text-success" aria-hidden="true" />
            ) : (
              <SkipForward className="size-4 text-muted-foreground" aria-hidden="true" />
            )}
            {assigneeUser ? (
              <span title={assignee} aria-label={assignee}>
                <Avatar
                  name={assigneeUser.name}
                  color={assigneeUser.color}
                  size="sm"
                  className="size-5 text-[0.5rem] ring-1"
                />
              </span>
            ) : (
              <span
                className="inline-grid size-5 place-items-center rounded-full bg-muted text-[0.5rem] font-extrabold text-muted-foreground"
                title={assignee}
                aria-label={assignee}
              >
                ?
              </span>
            )}
          </span>
          <span className="grid min-w-0 gap-0.5">
            <strong
              className={cn(
                'text-sm leading-snug font-bold break-words',
                !isOpen && 'line-through',
              )}
            >
              {task}
            </strong>
            <span className="flex min-w-0 flex-wrap items-center gap-1 text-xs text-muted-foreground">
              <span className="max-w-full rounded-md bg-secondary px-1.5 py-0.5 font-semibold text-secondary-foreground break-words">
                {roomName}
              </span>
              {!isOpen && <span>{t(occ.status === 'done' ? 'week.done' : 'week.skipped')}</span>}
            </span>
            {occ.isOverdue && (
              <Badge className="bg-warning text-warning-foreground">
                <TriangleAlert aria-hidden="true" />
                {t('today.overdue')}
              </Badge>
            )}
            {occ.movedFrom && (
              <span className="text-xs font-semibold text-muted-foreground">
                {format('week.movedFrom', { date: shortDay(occ.movedFrom) })}
              </span>
            )}
          </span>
        </span>
        <span
          className="grid size-9 shrink-0 place-content-center rounded-full border border-primary/25 bg-primary/10 text-center text-primary"
          aria-label={format('tasks.minutes', { minutes: occ.durationMinutesSnapshot })}
        >
          <strong className="text-xs leading-none tabular-nums">{occ.durationMinutesSnapshot}</strong>
          <span className="mt-0.5 text-[0.55rem] leading-none font-bold">min</span>
        </span>
        {isOpen && (
          <Button
            type="button"
            variant="outline"
            size="icon"
            className="size-9 shrink-0 rounded-full border-success/50 text-success hover:bg-success/10 hover:text-success"
            aria-label={format('today.completeNamed', { task })}
            onClick={() => onComplete(occ._id)}
          >
            {completionControl === 'thumb' ? <ThumbsUp aria-hidden="true" /> : <Circle aria-hidden="true" />}
          </Button>
        )}
        {occ.status === 'done' && (
          <Button
            type="button"
            variant="default"
            size="icon"
            className="size-9 shrink-0 rounded-full bg-success text-success-foreground hover:bg-success/90"
            aria-label={format('today.undoNamed', { task })}
            onClick={() => onUncomplete(occ._id)}
          >
            <Check aria-hidden="true" />
          </Button>
        )}
      </div>
    </li>
  );
}

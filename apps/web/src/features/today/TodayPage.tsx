import { ChevronLeft, ChevronRight, Plus, Sun, TriangleAlert, Undo2 } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { EmptyState } from '@/components/EmptyState';
import { useFilterReset } from '@/components/FilterReset';
import { NativeSelect } from '@/components/NativeSelect';
import { PageHeader } from '@/components/PageHeader';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import type { Occurrence } from '../../api/index.ts';
import { useSettings } from '../../api/v2/household.ts';
import { useCalendar, useRooms, useTasks } from '../../api/v2/queries.ts';
import { addDays, dayKeyInZone } from '@/lib/dayKey';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { getActiveProfileId } from '../../identity/profileStore.ts';
import { usePersistedFilter } from '../../hooks/usePersistedFilter.ts';
import { MyBadges } from '../badges/PersonBadges.tsx';
import { PromoteBanner } from '../promote/PromoteBanner.tsx';
import { CompletionChoiceDialog, useAssigneeChoice } from './CompletionChoiceDialog.tsx';
import {
  occurrenceKeys,
  useOccurrenceAction,
  useOccurrences,
  type OccurrenceAction,
} from './api.ts';
import { OccurrenceItem, shortDate } from './OccurrenceItem.tsx';
import { RecordWorkDialog } from './RecordWorkDialog.tsx';
import { longDay } from '../week/weekModel.ts';
import {
  groupToday,
  OVERDUE_LOOKBACK_DAYS,
  type TodayGroups,
} from './todayModel.ts';

export const UNDO_SNACKBAR_MS = 5000;

const SECTIONS: { key: keyof TodayGroups; title: MessageKey }[] = [
  { key: 'mine', title: 'today.mine' },
  { key: 'unclaimed', title: 'today.unclaimed' },
  { key: 'others', title: 'today.others' },
  { key: 'overdue', title: 'today.overdue' },
  { key: 'finished', title: 'today.finished' },
];

export function TodayPage({ now }: { now?: Date }) {
  const settings = useSettings();
  const tasks = useTasks();
  const rooms = useRooms();
  const { profile, activeUsers } = useProfile();
  const timezone = settings.data?.timezone ?? 'Europe/Amsterdam';
  const todayKey = dayKeyInZone(now ?? new Date(), timezone);
  const [dayOffset, setDayOffset] = useState(0);
  const defaultPersonFilter = profile?.id ?? getActiveProfileId() ?? 'all';
  const [personFilter, setPersonFilter, resetPersonFilter] = usePersistedFilter(
    'today.person', profile?.id ?? null, defaultPersonFilter,
  );
  useFilterReset(resetPersonFilter, personFilter !== defaultPersonFilter);
  const selectedDay = addDays(todayKey, dayOffset);
  const from = addDays(todayKey, -OVERDUE_LOOKBACK_DAYS);
  const occurrences = useOccurrences(from, selectedDay, settings.isSuccess);
  const calendar = useCalendar(selectedDay, selectedDay);
  const profileId = profile?.id ?? '';
  const action = useOccurrenceAction(occurrenceKeys.range(from, selectedDay), { profileId, todayKey });

  const [snackbar, setSnackbar] = useState<{ id: string; task: string; recorded?: true; plannedFor?: string } | null>(null);
  const [recordOpen, setRecordOpen] = useState(false);
  const [failed, setFailed] = useState(false);
  const [completionChoice, setCompletionChoice] = useState<Occurrence | null>(null);
  const choiceAssignee = useAssigneeChoice(completionChoice?.assigneeId ?? null);

  useEffect(() => {
    if (!snackbar) return;
    const timer = setTimeout(() => setSnackbar(null), UNDO_SNACKBAR_MS);
    return () => clearTimeout(timer);
  }, [snackbar]);

  const roomByTask = useMemo(() => {
    const roomNames = new Map((rooms.data ?? []).map((r) => [r.id, r.name]));
    return new Map((tasks.data ?? []).map((task) => [task.id, roomNames.get(task.roomId)]));
  }, [tasks.data, rooms.data]);

  if (settings.isPending || occurrences.isPending)
    return (
      <p role="status" className="py-10 text-center text-muted-foreground">
        {t('app.loading')}
      </p>
    );
  // A failed refetch (e.g. offline) keeps the last list on screen; only a first load without data is an error.
  if (settings.data === undefined || occurrences.data === undefined)
    return (
      <p role="alert" className="rounded-2xl bg-destructive/10 p-4 text-destructive">
        {t('app.error')}
      </p>
    );

  const run = (next: OccurrenceAction, occ?: Occurrence) => {
    setFailed(false);
    if (next.kind === 'complete' && occ) setSnackbar({ id: occ.id, task: occ.taskNameSnapshot });
    if (next.kind === 'uncomplete' || next.kind === 'retract') setSnackbar(null);
    action.mutate(next, {
      onError: () => {
        setFailed(true);
        setSnackbar(null);
      },
    });
  };

  const requestComplete = (occ: Occurrence) => {
    if (occ.assigneeId && occ.assigneeId !== profileId) {
      setCompletionChoice(occ);
      return;
    }
    run({ id: occ.id, kind: 'complete' }, occ);
  };

  const filteredOccurrences = occurrences.data.filter((occurrence) =>
    personFilter === 'all'
      ? true
      : personFilter === 'unassigned'
        ? occurrence.assigneeId === null
        : occurrence.assigneeId === personFilter,
  );
  const visibleOccurrences = dayOffset === 0
    ? filteredOccurrences
    : filteredOccurrences.filter((occurrence) => occurrence.date === selectedDay);
  const groupOwnerId = personFilter !== 'all' && personFilter !== 'unassigned'
    ? personFilter
    : profileId;
  const groups = groupToday(
    visibleOccurrences,
    groupOwnerId,
    selectedDay,
    settings.data.cycleAnchorDate,
  );
  const selectedPerson = activeUsers.find((user) => user.id === personFilter);
  // Which cycle week the day is comes from the server; a day before the anchor belongs to no cycle yet.
  const calendarDay = calendar.data?.get(selectedDay);
  const cycleLabel = !calendarDay
    ? ''
    : calendarDay.cycleIndex < 0
      ? format('cycle.startsOn', { date: shortDate(settings.data.cycleAnchorDate) })
      : format('cycle.week', { week: calendarDay.weekIndex + 1 });
  const nothingOpen =
    groups.mine.length + groups.unclaimed.length + groups.others.length + groups.overdue.length ===
    0;

  return (
    <section className="flex flex-col gap-6">
      <PageHeader
        title={t('nav.today')}
        description={cycleLabel ? `${longDay(selectedDay)} · ${cycleLabel}` : longDay(selectedDay)}
        className="mb-0"
        actions={
          <Button type="button" className="h-11 rounded-full" onClick={() => setRecordOpen(true)}>
            <Plus aria-hidden="true" />
            {t('recordWork.open')}
          </Button>
        }
      />
      <div className="grid gap-3 rounded-2xl border bg-card p-3 shadow-sm sm:grid-cols-[minmax(0,1fr)_12rem] sm:items-center">
        <div
          className="grid grid-cols-[2.5rem_repeat(3,minmax(0,1fr))_2.5rem] gap-1 rounded-xl bg-muted p-1"
          role="group"
          aria-label={t('today.dayNavigation')}
        >
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            className="rounded-lg"
            aria-label={t('today.previousDay')}
            disabled={dayOffset === 0}
            onClick={() => setDayOffset((offset) => Math.max(0, offset - 1))}
          >
            <ChevronLeft aria-hidden="true" />
          </Button>
          {[
            { offset: 0, label: t('nav.today') },
            { offset: 1, label: t('today.tomorrow') },
            { offset: 2, label: t('today.dayAfterTomorrow') },
          ].map((day) => (
            <Button
              key={day.offset}
              type="button"
              variant="ghost"
              size="sm"
              className={cn(
                'min-w-0 rounded-lg px-1 text-xs shadow-none sm:text-sm',
                dayOffset === day.offset && 'bg-background text-foreground shadow-sm hover:bg-background',
              )}
              aria-pressed={dayOffset === day.offset}
              onClick={() => setDayOffset(day.offset)}
            >
              {day.label}
            </Button>
          ))}
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            className="rounded-lg"
            aria-label={t('today.nextDay')}
            onClick={() => setDayOffset((offset) => offset + 1)}
          >
            <ChevronRight aria-hidden="true" />
          </Button>
        </div>
        <NativeSelect
          aria-label={t('today.filterPerson')}
          value={personFilter}
          onChange={(event) => setPersonFilter(event.target.value)}
        >
          <option value="all">{t('today.allPeople')}</option>
          {activeUsers.map((user) => <option key={user.id} value={user.id}>{user.name}</option>)}
          <option value="unassigned">{t('today.anyone')}</option>
        </NativeSelect>
      </div>
      <PromoteBanner />
      {failed && (
        <p
          role="alert"
          className="flex items-center gap-2 rounded-2xl bg-destructive/10 p-4 font-semibold text-destructive"
        >
          <TriangleAlert className="size-5 shrink-0" aria-hidden="true" />
          {t('today.actionError')}
        </p>
      )}
      {nothingOpen && (
        <EmptyState icon={<Sun className="size-6" aria-hidden="true" />}>
          {t('today.empty')}
        </EmptyState>
      )}

      <div
        data-testid="today-sections"
        className={cn(
          'flex flex-col gap-6 empty:hidden',
          // Everyone's tasks side by side once each column is wide enough to read; balanced
          // columns let each group stack without leaving row gaps next to a long group.
          personFilter === 'all'
            && 'lg:block lg:columns-2 lg:gap-6 lg:[&>section]:mb-6 lg:[&>section]:break-inside-avoid',
        )}
      >
        {SECTIONS.map(({ key, title }) =>
          groups[key].length === 0 ? null : (
            <section key={key} className="grid gap-3" aria-labelledby={`today-${key}`}>
              <div className="flex items-center gap-2 px-1">
                <h2
                  id={`today-${key}`}
                  className={cn(
                    'text-lg font-extrabold',
                    key === 'finished' && 'text-muted-foreground',
                  )}
                >
                  {key === 'mine' && selectedPerson && selectedPerson.id !== profileId
                    ? format('today.personTasks', { name: selectedPerson.name })
                    : t(title)}
                </h2>
                <Badge
                  variant="secondary"
                  className={cn(
                    'min-w-6 rounded-full',
                    key === 'overdue' && 'bg-warning text-warning-foreground',
                  )}
                >
                  {groups[key].length}
                </Badge>
              </div>
              <ul className="grid gap-3">
                {groups[key].map((occ) => (
                  <OccurrenceItem
                    key={occ.id}
                    occurrence={occ}
                    todayKey={todayKey}
                    roomName={occ.roomNameSnapshot ?? (occ.taskId ? roomByTask.get(occ.taskId) : undefined)}
                    users={activeUsers}
                    completionControl={settings.data.completionControl ?? 'circle'}
                    onComplete={() => requestComplete(occ)}
                    onUncomplete={() => run({ id: occ.id, kind: 'uncomplete' }, occ)}
                    onRetract={() => run({ id: occ.id, kind: 'retract' }, occ)}
                    onSkip={(reason) => run({ id: occ.id, kind: 'skip', reason }, occ)}
                    onClaim={() => run({ id: occ.id, kind: 'claim' }, occ)}
                    onAssign={(assigneeId) => run({ id: occ.id, kind: 'assign', assigneeId }, occ)}
                  />
                ))}
              </ul>
            </section>
          ),
        )}
      </div>

      {completionChoice?.assigneeId && (
        <CompletionChoiceDialog
          task={completionChoice.taskNameSnapshot}
          assignee={choiceAssignee.name}
          assigneeActive={choiceAssignee.active}
          open
          onOpenChange={(open) => {
            if (!open) setCompletionChoice(null);
          }}
          onCompleteForAssignee={() => {
            run(
              {
                id: completionChoice.id,
                kind: 'complete',
                completedBy: completionChoice.assigneeId ?? undefined,
              },
              completionChoice,
            );
            setCompletionChoice(null);
          }}
          onTakeOver={() => {
            run({ id: completionChoice.id, kind: 'complete', takeOver: true }, completionChoice);
            setCompletionChoice(null);
          }}
        />
      )}

      <MyBadges personId={profile?.id ?? null} />

      <RecordWorkDialog
        open={recordOpen}
        onOpenChange={setRecordOpen}
        todayKey={todayKey}
        onRecorded={(recorded, how) => {
          // Recorded work is dated today: show it, and offer the undo (a retract) like a check-off.
          setDayOffset(0);
          setFailed(false);
          // A checked-off planned task is undone like any check-off; recorded work is undone with a retract.
          // Planned work is an open occurrence like any other: it has no undo here.
          setSnackbar(
            how === 'planned'
              ? { id: recorded.id, task: recorded.taskNameSnapshot, plannedFor: recorded.date }
              : how === 'recorded'
                ? { id: recorded.id, task: recorded.taskNameSnapshot, recorded: true }
                : { id: recorded.id, task: recorded.taskNameSnapshot },
          );
        }}
      />

      {snackbar && (
        <div
          className="snackbar fixed inset-x-4 bottom-24 z-30 mx-auto flex max-w-md items-center justify-between gap-3 rounded-full bg-foreground py-1.5 pr-1.5 pl-5 text-background shadow-lg"
          role="status"
        >
          <span className="min-w-0 truncate text-sm font-semibold">
            {snackbar.plannedFor
              ? format('recordWork.scheduled', { task: snackbar.task, date: shortDate(snackbar.plannedFor) })
              : format(snackbar.recorded ? 'recordWork.recorded' : 'today.snackbar', { task: snackbar.task })}
          </span>
          {!snackbar.plannedFor && (
          <Button
            type="button"
            variant="ghost"
            className="h-11 shrink-0 rounded-full bg-background/15 px-4 font-bold text-background hover:bg-background/25 hover:text-background"
            onClick={() => {
              // Recorded work is retracted by its stored id; it does not have to be in the current list yet.
              if (snackbar.recorded) {
                run({ id: snackbar.id, kind: 'retract' });
                return;
              }
              const occ = occurrences.data.find((o) => o.id === snackbar.id);
              if (occ) run({ id: occ.id, kind: 'uncomplete' }, occ);
            }}
          >
            <Undo2 aria-hidden="true" />
            {t('today.undo')}
          </Button>
          )}
        </div>
      )}
    </section>
  );
}

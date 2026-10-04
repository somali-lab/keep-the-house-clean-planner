import {
  DndContext,
  KeyboardSensor,
  MeasuringStrategy,
  PointerSensor,
  TouchSensor,
  useSensor,
  useSensors,
  type DragEndEvent,
} from '@dnd-kit/core';
import type { Interval, User } from '../../api/v2/household.ts';
import { Ban, Menu, X } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useFilterReset } from '@/components/FilterReset';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/NativeSelect';
import { cn } from '@/lib/utils';
import { isStaleEntity } from '../../api/index.ts';
import type { Room, Task } from '../../api/v2/queries.ts';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import { usePersistedFilter } from '../../hooks/usePersistedFilter.ts';
import { useDebouncedValue } from '../../hooks/useDebouncedValue.ts';
import {
  EMPTY_SUMMARY,
  slotsBody,
  usePlanValidation,
  usePutSlots,
  useUpdatePlan,
  type CyclePlan,
  type PlanSlot as Slot,
} from './api.ts';
import {
  applyDrop,
  describePlanIssue,
  parseDragId,
  parseDropId,
  type DragSource,
  type DropRejection,
  type DropTarget,
} from './editorModel.ts';
import { WeekTable } from './PlanGrid.tsx';
import { Pool } from './Pool.tsx';

export interface PlanEditorProps {
  plan: CyclePlan;
  /** The active tasks the pool offers. */
  tasks: Task[];
  /** Every task, to name the ones a validation error is about; defaults to `tasks`. */
  allTasks?: Task[];
  rooms: Room[];
  users: User[];
  profileId: string | null;
  intervals: Interval[];
  /** How long the editor waits after the last drop before it saves. */
  debounceMs?: number;
  /** How long it waits before the server validates the unsaved slots. */
  validationDebounceMs?: number;
  onManagePlans?(): void;
  /** Raised when the page replaces the slots (a reset starts): a drop still waiting for its debounce is dropped, not saved over it. */
  discardPendingToken?: number;
}

type SaveState = 'idle' | 'saving' | 'saved' | 'error' | 'stale';

export function rejectionText(rejection: DropRejection): string {
  if (rejection.reason === 'assignee_unavailable') {
    return format('planner.reject.unavailable', {
      user: rejection.userName,
      weekday: t(`weekdayLong.${rejection.weekday}` as MessageKey),
      task: rejection.taskName,
    });
  }
  return format('planner.reject.duplicate', { task: rejection.taskName });
}

const sameSlots = (a: readonly Slot[], b: readonly Slot[]) => JSON.stringify(slotsBody(a)) === JSON.stringify(slotsBody(b));

export function PlanEditor({
  plan,
  tasks,
  allTasks = tasks,
  rooms,
  users,
  intervals,
  profileId,
  debounceMs = 800,
  validationDebounceMs = 300,
  onManagePlans,
  discardPendingToken = 0,
}: PlanEditorProps) {
  const [slots, setSlots] = useState<Slot[]>(plan.slots);
  const [themes, setThemes] = useState<string[]>(plan.weekThemes);
  const [selectedWeek, setSelectedWeek, resetWeek] = usePersistedFilter(
    'planner.week',
    profileId,
    0,
  );
  const [assigneeFilter, setAssigneeFilter, resetAssignee] = usePersistedFilter(
    'planner.assignee',
    profileId,
    'all',
  );
  const [roomFilter, setRoomFilter, resetRoom] = usePersistedFilter(
    'planner.room',
    profileId,
    'all',
  );
  const [intervalFilter, setIntervalFilter, resetInterval] = usePersistedFilter(
    'planner.interval',
    profileId,
    'all',
  );
  const [searchTerm, setSearchTerm, resetSearch] = usePersistedFilter(
    'planner.search',
    profileId,
    '',
  );
  useFilterReset(
    () => {
      resetWeek();
      resetAssignee();
      resetRoom();
      resetInterval();
      resetSearch();
    },
    selectedWeek !== 0
      || assigneeFilter !== 'all'
      || roomFilter !== 'all'
      || intervalFilter !== 'all'
      || searchTerm !== '',
  );
  const [poolCollapsed, setPoolCollapsed] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [saveState, setSaveState] = useState<SaveState>('idle');
  // A save that came back stale (412) wrote nothing: the person's edit stays here until they save it again.
  const staleSlots = useRef(false);
  const staleThemes = useRef(false);
  const [staleShown, setStaleShown] = useState(false);
  const markStale = (kind: 'slots' | 'themes', on: boolean) => {
    (kind === 'slots' ? staleSlots : staleThemes).current = on;
    setStaleShown(staleSlots.current || staleThemes.current);
  };
  const putSlots = usePutSlots();
  const updatePlan = useUpdatePlan();

  // Every write of the editor changes the plan and raises its version, so the writes go one at a time, each with the version the
  // one before answered (or, after a stale answer, the version of the plans read again).
  const versionRef = useRef(plan.version);
  useEffect(() => {
    versionRef.current = plan.version;
  }, [plan.version]);
  const queue = useRef<Promise<void>>(Promise.resolve());
  // Saves that are queued or in flight: while there are any, the plan the editor gets back is older than what the person sees.
  const inFlight = useRef(0);
  const enqueue = (job: () => Promise<void>) => {
    inFlight.current += 1;
    const run = () => job().finally(() => {
      inFlight.current -= 1;
    });
    queue.current = queue.current.then(run, run);
  };

  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pending = useRef<Slot[] | null>(null);
  const saveRef = useRef<(next: Slot[]) => void>(() => undefined);
  saveRef.current = (next: Slot[]) => {
    pending.current = null;
    setSaveState('saving');
    enqueue(async () => {
      try {
        const result = await putSlots.mutateAsync({ planId: plan.id, version: versionRef.current, slots: next });
        versionRef.current = result.plan.version;
        markStale('slots', false);
        setSaveState('saved');
      } catch (error) {
        if (isStaleEntity(error)) {
          markStale('slots', true);
          setSaveState('stale');
        } else {
          setSaveState('error');
        }
      }
    });
  };

  const saveThemes = (next: string[]) => {
    setSaveState('saving');
    enqueue(async () => {
      try {
        const saved = await updatePlan.mutateAsync({ planId: plan.id, version: versionRef.current, patch: { weekThemes: next } });
        versionRef.current = saved.version;
        markStale('themes', false);
        setSaveState('saved');
      } catch (error) {
        if (isStaleEntity(error)) {
          markStale('themes', true);
          setSaveState('stale');
        } else {
          setSaveState('error');
        }
      }
    });
  };

  // Keep the editor in sync when another planner action replaces all slots (the page remounts the editor after a reset); an edit that is
  // waiting to be saved, or that came back stale, is the person's and is not replaced.
  useEffect(() => {
    if (staleSlots.current || pending.current || inFlight.current > 0) return;
    if (timer.current) clearTimeout(timer.current);
    timer.current = null;
    setSlots(plan.slots);
  }, [plan.slots]);

  useEffect(() => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = null;
    pending.current = null;
  }, [discardPendingToken]);

  // Flush a pending save when the editor goes away (e.g. switching plans).
  useEffect(
    () => () => {
      if (timer.current) clearTimeout(timer.current);
      if (pending.current) saveRef.current(pending.current);
    },
    [],
  );

  const scheduleSave = (next: Slot[]) => {
    pending.current = next;
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      timer.current = null;
      if (pending.current) saveRef.current(pending.current);
    }, debounceMs);
  };

  // The server validates what the editor shows, as the save would: the stored plan while nothing is changed, the draft otherwise.
  const validatedSlots = useDebouncedValue(slots, validationDebounceMs);
  const validation = usePlanValidation(plan.id, validatedSlots, sameSlots(validatedSlots, plan.slots));
  const summary = validation.data?.summary ?? EMPTY_SUMMARY;
  const hasTasksToDistribute = summary.tasks.some(
    (task) => task.required !== null && task.placed !== task.required,
  );
  const cycleUsers = users.map((user) => ({
    user,
    minutes: summary.weeks.reduce(
      (sum, week) => sum + (week.users.find((entry) => entry.userId === user.id)?.minutes ?? 0),
      0,
    ),
  }));
  const cycleTotal = summary.weeks.reduce(
    (sum, week) =>
      sum +
      week.users.reduce((weekSum, user) => weekSum + user.minutes, 0) +
      week.unassignedMinutes,
    0,
  );

  useEffect(() => {
    if (validation.data && !hasTasksToDistribute) setPoolCollapsed(true);
  }, [validation.data, hasTasksToDistribute]);

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
    useSensor(TouchSensor, { activationConstraint: { delay: 200, tolerance: 5 } }),
    useSensor(KeyboardSensor),
  );

  const handleDrop = (source: DragSource, target: DropTarget) => {
    const result = applyDrop(slots, source, target, { tasks, users });
    if (!result.ok) {
      setMessage(rejectionText(result.rejection));
      return;
    }
    if (!result.changed) return;
    setMessage(null);
    setSlots(result.slots);
    scheduleSave(result.slots);
  };

  const onDragEnd = ({ active, over }: DragEndEvent) => {
    if (!over) return;
    const source = parseDragId(String(active.id));
    const target = parseDropId(String(over.id));
    if (source && target) handleDrop(source, target);
  };

  const saveTheme = (weekIndex: number) => {
    if (themes[weekIndex] === plan.weekThemes[weekIndex]) return;
    saveThemes(themes);
  };

  const saveAgain = () => {
    if (staleSlots.current) saveRef.current(slots);
    if (staleThemes.current) saveThemes(themes);
  };

  const issueNames = {
    task: (id: string) => allTasks.find((task) => task.id === id)?.name ?? id,
    user: (id: string) => users.find((user) => user.id === id)?.name ?? id,
  };

  // Nothing to show before the server has answered once: the pool and the totals come from its summary.
  if (validation.isPending) {
    return (
      <p role="status" className="text-muted-foreground">
        {t('planner.validating')}
      </p>
    );
  }


  return (
    <DndContext
      sensors={sensors}
      measuring={{ droppable: { strategy: MeasuringStrategy.Always } }}
      onDragEnd={onDragEnd}
    >
      {message && (
        <div
          role="alert"
          className="sticky top-20 z-10 flex items-center justify-between gap-3 rounded-xl border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm font-semibold text-destructive shadow-sm backdrop-blur"
        >
          <span className="flex items-center gap-2">
            <Ban className="size-4 shrink-0" aria-hidden="true" />
            {message}
          </span>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            className="text-destructive hover:bg-destructive/10 hover:text-destructive"
            onClick={() => setMessage(null)}
          >
            <X aria-hidden="true" />
            {t('common.close')}
          </Button>
        </div>
      )}
      {staleShown && (
        <div
          role="alert"
          className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm font-semibold text-destructive"
        >
          <span>{t('app.staleEntity')}</span>
          <Button type="button" variant="outline" size="sm" onClick={saveAgain}>
            {t('planner.saveAgain')}
          </Button>
        </div>
      )}
      {validation.isError && (
        <p role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm font-semibold text-destructive">
          {t('planner.validationError')}
        </p>
      )}
      {validation.data && validation.data.issues.length > 0 && (
        <section
          role="alert"
          aria-labelledby="plan-issues-title"
          className="rounded-xl border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
        >
          <h2 id="plan-issues-title" className="text-sm font-bold">
            {t('planner.issues.title')}
          </h2>
          <ul className="mt-1 list-disc pl-5">
            {validation.data.issues.map((issue, index) => (
              <li key={`${issue.code}-${issue.slotIndex ?? index}-${index}`}>{describePlanIssue(issue, issueNames)}</li>
            ))}
          </ul>
        </section>
      )}
      <div className="grid gap-4">
        <div
          className="sticky top-16 z-20 flex flex-wrap items-center gap-2 rounded-2xl border bg-card/95 p-2 shadow-md backdrop-blur"
          role="group"
          aria-label={t('planner.chooseWeek')}
        >
          <div className="flex gap-0.5">
            {[0, 1, 2, 3].map((weekIndex) => (
              <Button
                key={weekIndex}
                type="button"
                variant={selectedWeek === weekIndex ? 'default' : 'ghost'}
                className="h-10 rounded-xl px-3"
                aria-pressed={selectedWeek === weekIndex}
                onClick={() => setSelectedWeek(weekIndex)}
              >
                {format('planner.week', { n: weekIndex + 1 })}
              </Button>
            ))}
          </div>
          <label className="min-w-32 flex-1">
            <span className="visually-hidden">{t('planner.searchTasks')}</span>
            <Input
              type="search"
              className="h-10 min-w-32"
              value={searchTerm}
              placeholder={t('planner.searchTasks')}
              aria-label={t('planner.searchTasks')}
              onChange={(event) => setSearchTerm(event.target.value)}
            />
          </label>
          <NativeSelect
            className="w-36"
            aria-label={t('planner.filterAssignee')}
            value={assigneeFilter}
            onChange={(event) => setAssigneeFilter(event.target.value)}
          >
            <option value="all">{t('planner.filterAllPeople')}</option>
            {users.map((user) => (
              <option key={user.id} value={user.id}>
                {user.name}
              </option>
            ))}
            <option value="unassigned">{t('planner.anyone')}</option>
          </NativeSelect>
          <span
            role="status"
            className={cn(
              'ml-auto text-sm text-muted-foreground',
              saveState === 'saved' && 'text-success',
              saveState === 'error' && 'font-semibold text-destructive',
            )}
          >
            {saveState === 'saving' && t('planner.saving')}
            {saveState === 'saved' && t('planner.saved')}
            {saveState === 'error' && t('planner.saveError')}
          </span>
          {onManagePlans && (
            <Button type="button" variant="outline" className="ml-1" onClick={onManagePlans}>
              <Menu aria-hidden="true" />
              {t('planner.manage')}
            </Button>
          )}
        </div>

        <section
          className="flex flex-wrap items-center gap-2 rounded-xl border bg-card px-3 py-2"
          aria-label={t('planner.cycleTotal')}
        >
          <strong className="mr-1 text-sm">{t('planner.distribution.cycle')}:</strong>
          {cycleUsers.map(({ user, minutes }) => (
            <span
              key={user.id}
              className="rounded-full bg-secondary px-3 py-1 text-xs font-semibold tabular-nums"
            >
              {format('planner.weekTotal', { name: user.name, minutes })}
            </span>
          ))}
          <span className="rounded-full bg-accent px-3 py-1 text-xs font-bold tabular-nums">
            {format('planner.distribution.total', { minutes: cycleTotal })}
          </span>
        </section>

        <div
          className={cn(
            'grid items-start gap-5 transition-[grid-template-columns]',
            poolCollapsed
              ? 'lg:grid-cols-[3.5rem_minmax(0,1fr)]'
              : 'lg:grid-cols-[17rem_minmax(0,1fr)]',
          )}
        >
          <Pool
            tasks={tasks}
            rooms={rooms}
            intervals={intervals}
            summary={summary.tasks}
            collapsed={poolCollapsed}
            onCollapsedChange={setPoolCollapsed}
            searchTerm={searchTerm}
            roomFilter={roomFilter}
            onRoomFilterChange={setRoomFilter}
            intervalFilter={intervalFilter}
            onIntervalFilterChange={setIntervalFilter}
          />
          <div className="min-w-0">
            <WeekTable
              weekIndex={selectedWeek}
              theme={themes[selectedWeek] ?? ''}
              onThemeChange={(value) =>
                setThemes((prev) => prev.map((v, i) => (i === selectedWeek ? value : v)))
              }
              onThemeCommit={() => saveTheme(selectedWeek)}
              slots={slots}
              tasks={tasks}
              rooms={rooms}
              users={
                assigneeFilter === 'all'
                  ? users
                  : users.filter((user) => user.id === assigneeFilter)
              }
              showUnassigned={assigneeFilter === 'all' || assigneeFilter === 'unassigned'}
              summary={summary}
              searchTerm={searchTerm}
              onRemoveSlot={(index) => handleDrop({ kind: 'slot', index }, { kind: 'pool' })}
            />
          </div>
        </div>
      </div>
    </DndContext>
  );
}

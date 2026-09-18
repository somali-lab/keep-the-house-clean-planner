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
import type { CyclePlan, Interval, Room, Slot, Task, User } from '@huishoudplanner/shared';
// Subpath import keeps Luxon and Zod out of the web bundle.
import { validatePlan } from '@huishoudplanner/shared/validation/plan';
import { Ban, Menu, X } from 'lucide-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { Button } from '@/components/ui/button';
import { NativeSelect } from '@/components/NativeSelect';
import { cn } from '@/lib/utils';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import { useUpdatePlan, usePutSlots } from './api.ts';
import {
  applyDrop,
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
  tasks: Task[];
  rooms: Room[];
  users: User[];
  intervals: Interval[];
  debounceMs?: number;
  onManagePlans?(): void;
}

type SaveState = 'idle' | 'saving' | 'saved' | 'error';

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

export function PlanEditor({ plan, tasks, rooms, users, intervals, debounceMs = 800, onManagePlans }: PlanEditorProps) {
  const [slots, setSlots] = useState<Slot[]>(plan.slots);
  const [themes, setThemes] = useState<string[]>(plan.weekThemes);
  const [selectedWeek, setSelectedWeek] = useState(0);
  const [assigneeFilter, setAssigneeFilter] = useState('all');
  const [poolCollapsed, setPoolCollapsed] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [saveState, setSaveState] = useState<SaveState>('idle');
  const putSlots = usePutSlots();
  const updatePlan = useUpdatePlan();

  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pending = useRef<Slot[] | null>(null);
  const saveRef = useRef<(next: Slot[]) => void>(() => undefined);
  saveRef.current = (next: Slot[]) => {
    pending.current = null;
    setSaveState('saving');
    putSlots.mutate(
      { planId: plan._id, slots: next },
      { onSuccess: () => setSaveState('saved'), onError: () => setSaveState('error') },
    );
  };

  // Keep the editor in sync when another planner action replaces all slots
  // (for example the reset button in the page toolbar).
  useEffect(() => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = null;
    pending.current = null;
    setSlots(plan.slots);
  }, [plan.slots]);

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

  const validation = useMemo(
    () => validatePlan({ slots, tasks, users, intervals }),
    [slots, tasks, users, intervals],
  );
  const hasTasksToDistribute = validation.summary.tasks.some(
    (task) => task.required !== null && task.placed !== task.required,
  );

  useEffect(() => {
    if (!hasTasksToDistribute) setPoolCollapsed(true);
  }, [hasTasksToDistribute]);

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
    updatePlan.mutate({ planId: plan._id, patch: { weekThemes: themes } });
  };

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
      <div className="grid gap-4">
        <div
          className="sticky top-16 z-20 flex flex-wrap items-center gap-2 rounded-2xl border bg-card/95 p-2 shadow-md backdrop-blur"
          role="group"
          aria-label={t('planner.chooseWeek')}
        >
          <span className="px-2 text-sm font-bold text-muted-foreground">
            {t('planner.chooseWeek')}:
          </span>
          {[0, 1, 2, 3].map((weekIndex) => (
            <Button
              key={weekIndex}
              type="button"
              variant={selectedWeek === weekIndex ? 'default' : 'ghost'}
              className="h-10 rounded-xl px-5"
              aria-pressed={selectedWeek === weekIndex}
              onClick={() => setSelectedWeek(weekIndex)}
            >
              {format('planner.week', { n: weekIndex + 1 })}
            </Button>
          ))}
          <NativeSelect
            className="w-48"
            aria-label={t('planner.filterAssignee')}
            value={assigneeFilter}
            onChange={(event) => setAssigneeFilter(event.target.value)}
          >
            <option value="all">{t('planner.filterAllPeople')}</option>
            {users.map((user) => (
              <option key={user._id} value={user._id}>
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

        <div
          className={cn(
            'grid items-start gap-5 transition-[grid-template-columns]',
            poolCollapsed ? 'lg:grid-cols-[3.5rem_minmax(0,1fr)]' : 'lg:grid-cols-[17rem_minmax(0,1fr)]',
          )}
        >
          <Pool
            tasks={tasks}
            rooms={rooms}
            intervals={intervals}
            summary={validation.summary.tasks}
            collapsed={poolCollapsed}
            onCollapsedChange={setPoolCollapsed}
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
                  : users.filter((user) => user._id === assigneeFilter)
              }
              showUnassigned={assigneeFilter === 'all' || assigneeFilter === 'unassigned'}
              summary={validation.summary}
              onRemoveSlot={(index) => handleDrop({ kind: 'slot', index }, { kind: 'pool' })}
            />
          </div>
        </div>
      </div>
    </DndContext>
  );
}

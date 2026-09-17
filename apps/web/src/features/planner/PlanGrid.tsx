import { useDraggable, useDroppable } from '@dnd-kit/core';
import type { Room, Slot, Task, User } from '@huishoudplanner/shared';
import { WEEKDAYS_MONDAY_FIRST, type PlanSummary } from '@huishoudplanner/shared/validation/plan';
import { CalendarDays, GripVertical, TriangleAlert, Users, UserX, X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { cn } from '@/lib/utils';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import { Avatar } from '../../identity/Avatar.tsx';
import { cellId, dragId, quickDayId } from './editorModel.ts';

interface WeekTableProps {
  weekIndex: number;
  theme: string;
  onThemeChange(value: string): void;
  onThemeCommit(): void;
  slots: Slot[];
  tasks: Task[];
  rooms: Room[];
  users: User[];
  showUnassigned: boolean;
  showQuickDays: boolean;
  summary: PlanSummary;
  onRemoveSlot(index: number): void;
}

/** One cycle week: seven compact day cards with a lane per person. */
export function WeekTable({
  weekIndex,
  theme,
  onThemeChange,
  onThemeCommit,
  slots,
  tasks,
  rooms,
  users,
  showUnassigned,
  showQuickDays,
  summary,
  onRemoveSlot,
}: WeekTableProps) {
  const taskById = new Map(tasks.map((task) => [task._id, task]));
  const roomById = new Map(rooms.map((room) => [room._id, room.name]));
  const weekNumber = weekIndex + 1;
  const weekTotals = summary.weeks[weekIndex];
  const weekDays = summary.days.filter((day) => day.weekIndex === weekIndex);
  const minutesFor = (userId: string, weekdays: readonly number[]) =>
    weekDays
      .filter((day) => weekdays.includes(day.weekday))
      .reduce((total, day) => total + (day.users.find((user) => user.userId === userId)?.minutes ?? 0), 0);

  return (
    <section
      className="overflow-hidden rounded-2xl border bg-card shadow-sm"
      aria-label={format('planner.week', { n: weekNumber })}
    >
      <div className="flex flex-wrap items-center gap-3 border-b bg-secondary/25 px-4 py-3">
        <h2 className="flex items-center gap-2">
          <span className="grid size-8 place-items-center rounded-lg bg-primary/10 text-primary">
            <CalendarDays className="size-4" aria-hidden="true" />
          </span>
          {format('planner.week', { n: weekNumber })}
        </h2>
        <label className="min-w-48 flex-1">
          <span className="visually-hidden">{format('planner.weekTheme', { n: weekNumber })}</span>
          <Input
            value={theme}
            placeholder={format('planner.weekTheme', { n: weekNumber })}
            aria-label={format('planner.weekTheme', { n: weekNumber })}
            className="h-9 max-w-sm border-dashed bg-background/60 italic placeholder:not-italic"
            onChange={(e) => onThemeChange(e.target.value)}
            onBlur={onThemeCommit}
          />
        </label>
      </div>
      {showQuickDays && (
        <div className="border-b bg-primary/5 px-3 py-2.5" role="status">
          <p className="mb-2 text-xs font-semibold text-primary">
            {t('planner.quickPlanHint')}
          </p>
          <div className="grid grid-cols-7 gap-1.5">
            {WEEKDAYS_MONDAY_FIRST.map((weekday) => (
              <QuickDayTarget key={weekday} weekIndex={weekIndex} weekday={weekday} />
            ))}
          </div>
        </div>
      )}
      <div className="grid gap-2 border-b bg-secondary/15 px-3 py-3 sm:grid-cols-2">
        {users.map((user) => {
          const weekdayMinutes = minutesFor(user._id, [1, 2, 3, 4, 5]);
          const weekendMinutes = minutesFor(user._id, [6, 0]);
          const overBudget =
            weekdayMinutes > user.dailyBudgetMinutes.weekday || weekendMinutes > user.dailyBudgetMinutes.weekend;
          return (
          <div
            key={user._id}
            className={cn(
              'flex min-w-0 items-center gap-3 rounded-xl border bg-card px-3 py-2',
              overBudget && 'border-warning bg-warning/15',
            )}
          >
            <Avatar name={user.name} color={user.color} size="md" />
            <div className="min-w-0 flex-1">
              <strong className="block truncate text-sm">{user.name}</strong>
              <div className="mt-1 flex flex-wrap gap-x-3 gap-y-1 text-xs font-semibold text-muted-foreground tabular-nums">
                <span>
                  {format('planner.weekdayBudget', {
                    minutes: weekdayMinutes,
                    budget: user.dailyBudgetMinutes.weekday,
                  })}
                </span>
                <span>
                  {format('planner.weekendBudget', {
                    minutes: weekendMinutes,
                    budget: user.dailyBudgetMinutes.weekend,
                  })}
                </span>
              </div>
            </div>
          </div>
          );
        })}
      </div>
      <div className="p-3">
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2 lg:grid-cols-3">
          {WEEKDAYS_MONDAY_FIRST.map((weekday) => {
            const day = summary.days.find(
              (candidate) => candidate.weekIndex === weekIndex && candidate.weekday === weekday,
            );
            return (
              <section key={weekday} className="min-w-0 rounded-xl border bg-background/40 p-1.5">
                <h3 className="mb-1.5 rounded-lg bg-secondary px-2 py-1.5 text-center text-sm font-extrabold text-secondary-foreground">
                  {t(`weekdayLong.${weekday}` as MessageKey)}
                </h3>
                <div className="grid gap-1.5">
                  {[...users, ...(showUnassigned ? [null] : [])].map((user) => (
                    <Cell
                      key={user?._id ?? 'any'}
                      weekIndex={weekIndex}
                      weekday={weekday}
                      user={user}
                      slots={slots}
                      taskById={taskById}
                      roomById={roomById}
                      minutes={user ? day?.users.find((u) => u.userId === user._id) : undefined}
                      unassignedMinutes={day?.unassignedMinutes ?? 0}
                      onRemoveSlot={onRemoveSlot}
                    />
                  ))}
                </div>
              </section>
            );
          })}
        </div>
      </div>
      {weekTotals && (
        <p className="flex flex-wrap gap-2 border-t px-4 py-3 text-xs">
          {weekTotals.users.map((u) => {
            const user = users.find((candidate) => candidate._id === u.userId);
            return (
              <span
                key={u.userId}
                className="inline-flex items-center gap-1.5 rounded-full bg-secondary px-3 py-1 font-semibold text-secondary-foreground"
              >
                {user && <Avatar name={user.name} color={user.color} size="sm" />}
                {format('planner.weekTotal', { name: user?.name ?? '', minutes: u.minutes })}
              </span>
            );
          })}
          {showUnassigned && (
            <span className="inline-flex items-center gap-1.5 rounded-full bg-accent px-3 py-1 font-semibold text-accent-foreground">
              <Users className="size-3" aria-hidden="true" />
              {format('planner.weekUnassigned', { minutes: weekTotals.unassignedMinutes })}
            </span>
          )}
        </p>
      )}
    </section>
  );
}

function QuickDayTarget({ weekIndex, weekday }: { weekIndex: number; weekday: number }) {
  const { setNodeRef, isOver } = useDroppable({ id: quickDayId(weekIndex, weekday) });
  const day = t(`weekdayLong.${weekday}` as MessageKey);
  return (
    <div
      ref={setNodeRef}
      data-testid={quickDayId(weekIndex, weekday)}
      aria-label={format('planner.quickPlanDay', { day })}
      className={cn(
        'grid min-h-12 place-items-center rounded-xl border-2 border-dashed bg-card px-1 text-center text-xs font-extrabold transition-all',
        isOver && 'border-solid border-primary bg-primary/10 text-primary ring-2 ring-primary/20',
      )}
    >
      {day}
    </div>
  );
}

interface CellProps {
  weekIndex: number;
  weekday: number;
  user: User | null;
  slots: Slot[];
  taskById: Map<string, Task>;
  roomById: Map<string, string>;
  minutes: { minutes: number; budget: number; overBudget: boolean } | undefined;
  unassignedMinutes: number;
  onRemoveSlot(index: number): void;
}

function Cell({
  weekIndex,
  weekday,
  user,
  slots,
  taskById,
  roomById,
  minutes,
  unassignedMinutes,
  onRemoveSlot,
}: CellProps) {
  const id = cellId(weekIndex, weekday, user?._id ?? null);
  const { setNodeRef, isOver } = useDroppable({ id });
  const items = slots
    .map((slot, index) => ({ slot, index }))
    .filter(
      ({ slot }) =>
        slot.weekIndex === weekIndex &&
        slot.weekday === weekday &&
        slot.assigneeId === (user?._id ?? null),
    );
  const unavailable = user?.unavailableWeekdays.includes(weekday) ?? false;
  const overBudget = minutes?.overBudget ?? false;

  return (
    <div
      ref={setNodeRef}
      className={cn(
        'plan-cell min-w-0 rounded-lg border bg-card p-1.5 transition-colors',
        !user && 'bg-accent/40',
        unavailable &&
          'bg-[repeating-linear-gradient(45deg,var(--muted),var(--muted)_6px,var(--card)_6px,var(--card)_12px)]',
        overBudget && 'is-over-budget border-2 border-warning bg-warning/15',
        isOver && 'is-over bg-primary/5 ring-2 ring-primary ring-inset',
      )}
      data-testid={id}
    >
      <div className="flex min-h-14 flex-col gap-1">
        {unavailable && user && (
          <span className="flex min-w-0 items-center gap-1.5 rounded-md bg-card/85 px-1.5 py-1 text-[0.7rem] font-bold text-muted-foreground">
            <Avatar name={user.name} color={user.color} size="sm" />
            <UserX className="size-3.5 shrink-0 text-destructive" aria-hidden="true" />
            <span className="truncate">{format('planner.unavailablePerson', { name: user.name })}</span>
          </span>
        )}
        {!user && (
          <span className="flex min-w-0 items-center gap-1 border-b pb-1 text-[0.7rem] font-bold text-muted-foreground">
            <Users className="size-3 shrink-0" aria-hidden="true" />
            <span className="truncate">{t('planner.anyone')}</span>
          </span>
        )}
        <ul className="flex flex-1 flex-col gap-1">
          {items.map(({ slot, index }) => (
            <SlotItem
              key={`${slot.taskId}-${index}`}
              index={index}
              task={taskById.get(slot.taskId)}
              roomName={roomById.get(taskById.get(slot.taskId)?.roomId ?? '')}
              assignee={user}
              fallbackName={slot.taskId}
              onRemove={() => onRemoveSlot(index)}
            />
          ))}
        </ul>
        <span
          className={cn(
            'flex items-center gap-1 px-0.5 text-[0.7rem] font-semibold text-muted-foreground tabular-nums',
            overBudget && 'text-warning-foreground',
          )}
        >
          {!unavailable &&
            (minutes
              ? format('planner.dayMinutes', { minutes: minutes.minutes })
              : format('planner.unassignedMinutes', { minutes: unassignedMinutes }))}
        </span>
        {overBudget && (
          <strong className="flex items-center gap-1 rounded-md bg-warning/40 px-1.5 py-0.5 text-[0.7rem] font-bold text-warning-foreground">
            <TriangleAlert className="size-3 shrink-0" aria-hidden="true" />
            {t('planner.overBudget')}
          </strong>
        )}
      </div>
    </div>
  );
}

function SlotItem({
  index,
  task,
  roomName,
  assignee,
  fallbackName,
  onRemove,
}: {
  index: number;
  task: Task | undefined;
  roomName: string | undefined;
  assignee: User | null;
  fallbackName: string;
  onRemove(): void;
}) {
  const { attributes, listeners, setNodeRef, setActivatorNodeRef, transform, isDragging } =
    useDraggable({
      id: dragId({ kind: 'slot', index }),
    });
  const name = task?.name ?? fallbackName;
  const style = transform
    ? { transform: `translate3d(${transform.x}px, ${transform.y}px, 0)` }
    : undefined;
  return (
    <li
      ref={setNodeRef}
      style={style}
      className={cn(
        'group flex items-start gap-0.5 rounded-lg border bg-card py-1 pr-0.5 pl-1 text-xs shadow-xs',
        isDragging && 'relative z-20 opacity-60 shadow-md',
      )}
    >
      <span
        ref={setActivatorNodeRef}
        className="flex min-w-0 flex-1 cursor-grab items-start gap-0.5 rounded-md outline-none focus-visible:ring-2 focus-visible:ring-ring/50 active:cursor-grabbing"
        {...listeners}
        {...attributes}
      >
        <GripVertical className="mt-0.5 size-3 shrink-0 text-muted-foreground" aria-hidden="true" />
        {assignee && (
          <span title={assignee.name} aria-label={assignee.name}>
            <Avatar
              name={assignee.name}
              color={assignee.color}
              size="sm"
              className="size-[1.125rem] text-[0.5rem] ring-1"
            />
          </span>
        )}
        <span className="min-w-0 flex-1">
          <span className="block truncate font-semibold" title={name}>
            {name}
          </span>
          {task && (
            <small className="block truncate text-[0.7rem] text-muted-foreground" title={roomName}>
              {roomName}
            </small>
          )}
        </span>
      </span>
      <Button
        type="button"
        variant="ghost"
        size="icon-xs"
        className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
        aria-label={format('planner.removeSlot', { task: name })}
        onClick={onRemove}
      >
        <X aria-hidden="true" />
      </Button>
      {task && (
        <span
          className="grid size-7 shrink-0 place-content-center rounded-full border border-primary/25 bg-primary/10 text-center text-primary"
          aria-label={format('tasks.minutes', { minutes: task.durationMinutes })}
        >
          <strong className="text-[0.65rem] leading-none tabular-nums">{task.durationMinutes}</strong>
          <span className="text-[0.4rem] leading-none font-bold">min</span>
        </span>
      )}
    </li>
  );
}

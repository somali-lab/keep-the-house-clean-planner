import { useDraggable, useDroppable } from '@dnd-kit/core';
import type { Interval, Room, Task, TaskSummary } from '@huishoudplanner/shared';
import { GripVertical, House, Inbox, PanelLeftClose, PanelLeftOpen, TriangleAlert } from 'lucide-react';
import { useState } from 'react';
import { NativeSelect } from '@/components/NativeSelect';
import { Badge } from '@/components/ui/badge';
import { cn } from '@/lib/utils';
import { format, t } from '../../i18n/nl.ts';
import { getLocale } from '../../i18n/runtime.ts';
import { dragId, POOL_ID } from './editorModel.ts';

interface PoolProps {
  tasks: Task[];
  rooms: Room[];
  intervals: Interval[];
  summary: TaskSummary[];
  collapsed: boolean;
  onCollapsedChange(collapsed: boolean): void;
}

/** Tasks that are not (exactly) placed yet; also the drop zone for removing slots. */
export function Pool({ tasks, rooms, intervals, summary, collapsed, onCollapsedChange }: PoolProps) {
  const { setNodeRef, isOver } = useDroppable({ id: POOL_ID });
  const [roomFilter, setRoomFilter] = useState('all');
  const [intervalFilter, setIntervalFilter] = useState('all');
  const byTask = new Map(summary.map((s) => [s.taskId, s]));
  const roomById = new Map(rooms.map((room) => [room._id, room]));
  const remaining = tasks
    .map((task) => ({
      task,
      stats: byTask.get(task._id) ?? { taskId: task._id, placed: 0, required: null },
    }))
    .filter(({ stats }) => stats.required === null || stats.placed !== stats.required);
  const open = remaining
    .filter(({ task }) => roomFilter === 'all' || task.roomId === roomFilter)
    .filter(({ task }) => intervalFilter === 'all' || task.intervalKey === intervalFilter)
    .sort((a, b) => {
      const roomCompare = (roomById.get(a.task.roomId)?.name ?? '').localeCompare(
        roomById.get(b.task.roomId)?.name ?? '',
        getLocale(),
      );
      return roomCompare || a.task.name.localeCompare(b.task.name, getLocale());
    });
  const usedRooms = rooms
    .filter((room) => tasks.some((task) => task.roomId === room._id))
    .sort((a, b) => a.sortOrder - b.sortOrder);
  const usedIntervals = intervals.filter((interval) =>
    tasks.some((task) => task.intervalKey === interval.key),
  );

  return (
    <aside
      ref={setNodeRef}
      className={cn(
        'flex min-w-0 flex-col gap-3 overflow-hidden rounded-2xl border-2 border-dashed bg-card p-4 shadow-sm transition-[width,color] lg:sticky lg:top-20 lg:max-h-[calc(100vh-6rem)]',
        collapsed && 'items-center px-2',
        isOver && 'is-over border-solid border-primary bg-primary/5',
      )}
      aria-labelledby="pool-title"
    >
      <div className={cn('flex w-full items-start gap-2', collapsed && 'flex-col items-center')}>
        <div className={cn('min-w-0 flex-1', collapsed && 'flex flex-col items-center gap-1')}>
          <h2 id="pool-title" className={cn('flex items-center gap-2', collapsed && 'visually-hidden')}>
            <Inbox className="size-5 text-primary" aria-hidden="true" />
            {t('planner.pool')}
          </h2>
          {collapsed && (
            <>
              <Inbox className="size-5 text-primary" aria-hidden="true" />
              <Badge variant="secondary" className="tabular-nums" title={t('planner.pool')}>
                {remaining.length}
              </Badge>
            </>
          )}
          {!collapsed && <p className="mt-1 text-xs leading-relaxed text-muted-foreground">{t('planner.poolHint')}</p>}
        </div>
        <button
          type="button"
          className="grid size-9 shrink-0 place-items-center rounded-lg text-muted-foreground hover:bg-accent hover:text-accent-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none"
          aria-label={collapsed ? t('planner.poolExpand') : t('planner.poolCollapse')}
          aria-expanded={!collapsed}
          onClick={() => onCollapsedChange(!collapsed)}
        >
          {collapsed ? <PanelLeftOpen className="size-5" aria-hidden="true" /> : <PanelLeftClose className="size-5" aria-hidden="true" />}
        </button>
      </div>
      {!collapsed && <div className="grid min-w-0 grid-cols-1 gap-2">
        <NativeSelect
          className="min-w-0 max-w-full"
          value={roomFilter}
          onChange={(event) => setRoomFilter(event.target.value)}
          aria-label={t('planner.filterRoom')}
        >
          <option value="all">{t('planner.allRooms')}</option>
          {usedRooms.map((room) => (
            <option key={room._id} value={room._id}>
              {room.name}
            </option>
          ))}
        </NativeSelect>
        <NativeSelect
          className="min-w-0 max-w-full"
          value={intervalFilter}
          onChange={(event) => setIntervalFilter(event.target.value)}
          aria-label={t('planner.filterInterval')}
        >
          <option value="all">{t('planner.allIntervals')}</option>
          {usedIntervals.map((interval) => (
            <option key={interval.key} value={interval.key}>
              {interval.label}
            </option>
          ))}
        </NativeSelect>
      </div>}
      {!collapsed && <ul className="planner-pool-scroll grid min-h-0 min-w-0 flex-1 auto-rows-max content-start gap-2 overflow-x-hidden overflow-y-auto pr-1">
        {open.map(({ task, stats }) => (
          <PoolItem
            key={task._id}
            task={task}
            roomName={roomById.get(task.roomId)?.name ?? t('tasks.unknownRoom')}
            placed={stats.placed}
            required={stats.required}
          />
        ))}
      </ul>}
    </aside>
  );
}

function PoolItem({
  task,
  roomName,
  placed,
  required,
}: {
  task: Task;
  roomName: string;
  placed: number;
  required: number | null;
}) {
  const { attributes, listeners, setNodeRef, transform, isDragging } = useDraggable({
    id: dragId({ kind: 'pool', taskId: task._id }),
  });
  const style = transform
    ? { transform: `translate3d(${transform.x}px, ${transform.y}px, 0)` }
    : undefined;
  const mismatch = required !== null && placed > 0 && placed !== required;
  const badge =
    required === null ? t('planner.notApplicable') : format('planner.placed', { placed, required });
  const badgeLabel =
    required === null
      ? t('planner.notApplicable')
      : format('planner.placedLabel', { placed, required });

  return (
    <li
      ref={setNodeRef}
      style={style}
      className={cn(
        'flex min-h-14 min-w-0 max-w-full cursor-grab items-center gap-2 overflow-hidden rounded-lg border bg-background px-2 py-2 text-sm shadow-xs transition-shadow outline-none hover:border-primary/40 hover:shadow-sm focus-visible:ring-[3px] focus-visible:ring-ring/50 active:cursor-grabbing',
        isDragging && 'relative z-20 opacity-60 shadow-md',
      )}
      data-testid={`pool-${task._id}`}
      {...listeners}
      {...attributes}
    >
      <GripVertical className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
      <span className="min-w-0 flex-1">
        <span className="block truncate font-semibold">{task.name}</span>
        <small className="flex min-w-0 items-center gap-1 text-xs text-muted-foreground">
          <House className="size-3 shrink-0" aria-hidden="true" />
          <span className="truncate">{roomName}</span>
          <span aria-hidden="true">·</span>
          <span className="shrink-0">{format('tasks.minutes', { minutes: task.durationMinutes })}</span>
        </small>
      </span>
      <span className="flex shrink-0 flex-col items-end gap-1">
        <Badge
          variant="outline"
          className={cn('tabular-nums', required === null && 'text-muted-foreground')}
          aria-label={badgeLabel}
        >
          {badge}
        </Badge>
        {mismatch && (
          <Badge
            className="border-warning bg-warning/30 text-warning-foreground"
            title={t('planner.intervalMismatch')}
          >
            <TriangleAlert aria-hidden="true" />
            <span className="visually-hidden">{t('planner.intervalMismatch')}</span>
          </Badge>
        )}
      </span>
    </li>
  );
}

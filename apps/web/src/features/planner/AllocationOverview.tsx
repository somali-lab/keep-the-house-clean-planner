import type { Interval, Room, Slot, Task, User } from '@huishoudplanner/shared';
import { CheckCircle2, CircleDashed, Scale, TriangleAlert } from 'lucide-react';
import type { CSSProperties } from 'react';
import { cn } from '@/lib/utils';
import { format, t } from '../../i18n/nl.ts';

interface AllocationOverviewProps {
  slots: Slot[];
  tasks: Task[];
  users: User[];
  rooms: Room[];
  intervals: Interval[];
  view: 'workload' | 'spacing';
}

interface AllocationRow {
  id: string;
  name: string;
  color: string | null;
  minutes: number;
  weekdayMinutes: number;
  weekendMinutes: number;
  weekdayBudget: number | null;
  weekendBudget: number | null;
}

const UNASSIGNED_ID = 'any';
const CYCLE_DAYS = 28;

export interface SpacingRow {
  taskId: string;
  name: string;
  roomName: string;
  intervalLabel: string;
  placed: number;
  required: number;
  expectedGap: string;
  actualGap: string | null;
  status: 'incomplete' | 'even' | 'uneven';
}

function gapText(minimum: number, maximum: number): string {
  return minimum === maximum ? String(minimum) : `${minimum}–${maximum}`;
}

export function spacingRows(
  slots: Slot[],
  tasks: Task[],
  intervals: Interval[],
  rooms: Room[],
): SpacingRow[] {
  const requiredByInterval = new Map(
    intervals.map((interval) => [interval.key, interval.perCycle]),
  );
  const roomNames = new Map(rooms.map((room) => [room._id, room.name]));
  const intervalLabels = new Map(intervals.map((interval) => [interval.key, interval.label]));

  return tasks.flatMap<SpacingRow>((task) => {
    const required = requiredByInterval.get(task.intervalKey);
    if (required === null || required === undefined || required <= 1) return [];

    const taskSlots = slots.filter((slot) => slot.taskId === task._id);
    const ideal = CYCLE_DAYS / required;
    const expectedGap = gapText(Math.floor(ideal), Math.ceil(ideal));
    if (taskSlots.length !== required) {
      return [
        {
          taskId: task._id,
          name: task.name,
          roomName: roomNames.get(task.roomId) ?? t('tasks.unknownRoom'),
          intervalLabel: intervalLabels.get(task.intervalKey) ?? task.intervalKey,
          placed: taskSlots.length,
          required,
          expectedGap,
          actualGap: null,
          status: 'incomplete',
        },
      ];
    }

    // Weekdays are stored as 0=Sunday..6=Saturday; translate to a Monday-first cycle position.
    const positions = taskSlots
      .map((slot) => slot.weekIndex * 7 + ((slot.weekday + 6) % 7))
      .sort((a, b) => a - b);
    const gaps = positions.map((position, index) => {
      const next = positions[(index + 1) % positions.length]!;
      return index === positions.length - 1 ? next + CYCLE_DAYS - position : next - position;
    });
    const minimum = Math.min(...gaps);
    const maximum = Math.max(...gaps);
    const tolerance = Math.max(1, ideal * 0.25);
    const even = gaps.every((gap) => Math.abs(gap - ideal) <= tolerance);

    return [
      {
        taskId: task._id,
        name: task.name,
        roomName: roomNames.get(task.roomId) ?? t('tasks.unknownRoom'),
        intervalLabel: intervalLabels.get(task.intervalKey) ?? task.intervalKey,
        placed: taskSlots.length,
        required,
        expectedGap,
        actualGap: gapText(minimum, maximum),
        status: even ? 'even' : 'uneven',
      },
    ];
  });
}

export function allocationRows(
  slots: Slot[],
  tasks: Task[],
  users: User[],
  budgetMultiplier = 1,
): AllocationRow[] {
  const minutesByTask = new Map(tasks.map((task) => [task._id, task.durationMinutes]));
  const totals = new Map<string, { weekday: number; weekend: number }>();

  for (const slot of slots) {
    const minutes = minutesByTask.get(slot.taskId) ?? 0;
    const id = slot.assigneeId ?? UNASSIGNED_ID;
    const current = totals.get(id) ?? { weekday: 0, weekend: 0 };
    const period = slot.weekday === 0 || slot.weekday === 6 ? 'weekend' : 'weekday';
    current[period] += minutes;
    totals.set(id, current);
  }

  return [
    ...users.map((user) => {
      const total = totals.get(user._id) ?? { weekday: 0, weekend: 0 };
      return {
        id: user._id,
        name: user.name,
        color: user.color,
        minutes: total.weekday + total.weekend,
        weekdayMinutes: total.weekday,
        weekendMinutes: total.weekend,
        weekdayBudget: user.dailyBudgetMinutes.weekday * budgetMultiplier,
        weekendBudget: user.dailyBudgetMinutes.weekend * budgetMultiplier,
      };
    }),
    {
      id: UNASSIGNED_ID,
      name: t('planner.anyone'),
      color: null,
      minutes:
        (totals.get(UNASSIGNED_ID)?.weekday ?? 0) + (totals.get(UNASSIGNED_ID)?.weekend ?? 0),
      weekdayMinutes: totals.get(UNASSIGNED_ID)?.weekday ?? 0,
      weekendMinutes: totals.get(UNASSIGNED_ID)?.weekend ?? 0,
      weekdayBudget: null,
      weekendBudget: null,
    },
  ];
}

function Distribution({ title, rows }: { title: string; rows: AllocationRow[] }) {
  const total = rows.reduce((sum, row) => sum + row.minutes, 0);
  const periods = [
    { key: 'weekday', label: t('distribution.weekday') },
    { key: 'weekend', label: t('distribution.weekend') },
  ] as const;

  return (
    <div className="rounded-xl border bg-background/60 p-4">
      <div className="mb-3 flex items-baseline justify-between gap-3">
        <h3 className="font-bold">{title}</h3>
        <span className="text-sm font-semibold text-muted-foreground">
          {format('planner.distribution.total', { minutes: total })}
        </span>
      </div>
      <div className="grid gap-3">
        {rows.map((row) => (
          <div key={row.id} className="rounded-lg border bg-card p-3">
            <div className="mb-2 flex items-center justify-between gap-3 text-sm">
              <span className="flex min-w-0 items-center gap-2">
                <span
                  className={cn(
                    'size-2.5 shrink-0 rounded-full',
                    row.color !== null && 'user-color',
                    row.color === null && 'border border-dashed border-muted-foreground',
                  )}
                  style={row.color ? ({ '--user-color': row.color } as CSSProperties) : undefined}
                  aria-hidden="true"
                />
                <strong className="truncate">{row.name}</strong>
              </span>
              <span className="shrink-0 font-semibold tabular-nums text-muted-foreground">
                {format('planner.distribution.personTotal', { minutes: row.minutes })}
              </span>
            </div>
            <div className="grid gap-2">
              {periods.map((period) => {
                const minutes = period.key === 'weekday' ? row.weekdayMinutes : row.weekendMinutes;
                const budget = period.key === 'weekday' ? row.weekdayBudget : row.weekendBudget;
                const percentage =
                  budget === null || budget === 0
                    ? minutes === 0
                      ? 0
                      : null
                    : Math.round((minutes / budget) * 100);
                const overBudget = budget !== null && minutes > budget;
                return (
                  <div
                    key={period.key}
                    className="grid grid-cols-[5.5rem_minmax(5rem,1fr)_7.5rem] items-center gap-2 text-xs"
                  >
                    <span className="font-semibold text-muted-foreground">{period.label}</span>
                    <div
                      className="h-2.5 overflow-hidden rounded-full bg-muted"
                      role="meter"
                      aria-label={format('planner.distribution.periodLoad', {
                        period: period.label.toLowerCase(),
                        name: row.name,
                      })}
                      aria-valuemin={0}
                      aria-valuemax={100}
                      aria-valuenow={Math.min(percentage ?? 0, 100)}
                      aria-valuetext={
                        budget === null
                          ? `${minutes} min`
                          : `${minutes} van ${budget} min (${percentage ?? 0}%)`
                      }
                    >
                      <div
                        className={cn(
                          'h-full rounded-full transition-[width]',
                          period.key === 'weekend' && !overBudget && 'opacity-60',
                          overBudget && 'bg-warning',
                          row.color === null && 'bg-muted-foreground',
                          !overBudget && row.color !== null && 'user-color',
                        )}
                        style={{
                          width: `${Math.min(percentage ?? 0, 100)}%`,
                          ...(!overBudget && row.color
                            ? ({ '--user-color': row.color } as CSSProperties)
                            : {}),
                        }}
                      />
                    </div>
                    <span
                      className={cn(
                        'text-right tabular-nums',
                        overBudget && 'font-bold text-warning-foreground',
                      )}
                    >
                      <strong>
                        {minutes}
                        {budget !== null && ` / ${budget}`} min
                      </strong>{' '}
                      <span className={cn(!overBudget && 'text-muted-foreground')}>
                        {percentage === null ? '—' : `(${percentage}%)`}
                      </span>
                    </span>
                  </div>
                );
              })}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

export function AllocationOverview({
  slots,
  tasks,
  users,
  rooms,
  intervals,
  view,
}: AllocationOverviewProps) {
  const weeks = [0, 1, 2, 3].map((weekIndex) => ({
    weekIndex,
    rows: allocationRows(
      slots.filter((slot) => slot.weekIndex === weekIndex),
      tasks,
      users,
    ),
  }));
  const cycleRows = allocationRows(slots, tasks, users, 4);
  const cadence = spacingRows(slots, tasks, intervals, rooms);
  const titleId =
    view === 'workload'
      ? 'planner-distribution-workload-title'
      : 'planner-distribution-spacing-title';

  return (
    <section className="rounded-2xl border bg-card p-4 shadow-sm" aria-labelledby={titleId}>
      <div className="mb-4 flex items-start gap-3">
        <div className="grid size-9 shrink-0 place-items-center rounded-xl bg-accent text-accent-foreground">
          <Scale className="size-4" aria-hidden="true" />
        </div>
        <div>
          <h2 id={titleId} className="font-bold">
            {view === 'workload' ? t('planner.distribution') : t('planner.spacing')}
          </h2>
          <p className="text-sm text-muted-foreground">
            {view === 'workload'
              ? t('planner.distribution.explainer')
              : t('planner.spacing.explainer')}
          </p>
        </div>
      </div>
      {view === 'workload' ? (
        <div className="grid gap-4">
          <div className="grid gap-4 xl:grid-cols-2">
            {weeks.map(({ weekIndex, rows }) => (
              <Distribution
                key={weekIndex}
                title={format('planner.distribution.week', { n: weekIndex + 1 })}
                rows={rows}
              />
            ))}
          </div>
          <Distribution title={t('planner.distribution.cycle')} rows={cycleRows} />
        </div>
      ) : cadence.length > 0 ? (
        <div className="grid gap-2">
          {cadence.map((row) => (
            <div
              key={row.taskId}
              className="grid gap-1 rounded-lg border bg-card px-3 py-2 sm:grid-cols-[minmax(8rem,1fr)_auto_auto] sm:items-center sm:gap-4"
            >
              <span className="grid min-w-0 gap-0.5">
                <strong className="truncate">{row.name}</strong>
                <span className="truncate text-xs font-normal text-muted-foreground">
                  {row.roomName} · {row.intervalLabel}
                </span>
              </span>
              <span className="text-sm text-muted-foreground">
                {format('planner.spacing.wanted', { days: row.expectedGap })}
                {row.actualGap !== null &&
                  ` · ${format('planner.spacing.actual', { days: row.actualGap })}`}
              </span>
              <span
                className={cn(
                  'flex items-center gap-1.5 text-sm font-semibold sm:justify-self-end',
                  row.status === 'even' && 'text-success',
                  row.status === 'uneven' && 'text-warning-foreground',
                  row.status === 'incomplete' && 'text-muted-foreground',
                )}
              >
                {row.status === 'even' && <CheckCircle2 className="size-4" aria-hidden="true" />}
                {row.status === 'uneven' && <TriangleAlert className="size-4" aria-hidden="true" />}
                {row.status === 'incomplete' && (
                  <CircleDashed className="size-4" aria-hidden="true" />
                )}
                {row.status === 'incomplete'
                  ? format('planner.spacing.incomplete', {
                      placed: row.placed,
                      required: row.required,
                    })
                  : t(`planner.spacing.${row.status}`)}
              </span>
            </div>
          ))}
        </div>
      ) : (
        <p className="rounded-xl border border-dashed px-4 py-3 text-sm text-muted-foreground">
          {t('planner.spacing.none')}
        </p>
      )}
    </section>
  );
}

import { weekIndexFor } from '@huishoudplanner/shared/cycle';
import { ListChecks } from 'lucide-react';
import { useMemo, useState } from 'react';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { useRooms, useSettings, useTasks } from '../../api/queries.ts';
import { format, t } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { useOccurrences } from '../today/api.ts';
import { addDaysKey, dayKeyInZone } from '../today/todayModel.ts';
import {
  compactDate,
  datedWeekday,
  taskOverviewRows,
  type TaskOverviewRow,
} from './taskOverviewModel.ts';

type WeekRange = 1 | 2 | 4;
const WEEK_RANGES: WeekRange[] = [1, 2, 4];

export function MobileTasksPage({ now }: { now?: Date }) {
  const settings = useSettings();
  const tasks = useTasks();
  const rooms = useRooms();
  const { profile } = useProfile();
  const [weeks, setWeeks] = useState<WeekRange>(1);
  const [hiddenRoomIds, setHiddenRoomIds] = useState<Set<string>>(() => new Set());
  const from = dayKeyInZone(now ?? new Date(), settings.data?.timezone ?? 'Europe/Amsterdam');
  const to = addDaysKey(from, weeks * 7 - 1);
  const cycleWeek = settings.data ? weekIndexFor(from, settings.data.cycleAnchorDate) + 1 : null;
  const occurrences = useOccurrences(from, to, settings.isSuccess);
  const rows = useMemo(() => {
    const relevant = (occurrences.data ?? []).filter(
      (occurrence) => occurrence.assigneeId === profile?._id || occurrence.assigneeId === null,
    );
    const build = (assigneeId: string | null) =>
      taskOverviewRows(
        relevant.filter((occurrence) => occurrence.assigneeId === assigneeId),
        tasks.data ?? [],
        rooms.data ?? [],
        t('tasks.unknownRoom'),
      );
    return { mine: build(profile?._id ?? ''), unassigned: build(null) };
  }, [occurrences.data, profile?._id, rooms.data, tasks.data]);
  const filterRooms = (items: TaskOverviewRow[]) =>
    items.filter((row) => row.roomId === null || !hiddenRoomIds.has(row.roomId));
  const visible = { mine: filterRooms(rows.mine), unassigned: filterRooms(rows.unassigned) };
  const allRows = [...rows.mine, ...rows.unassigned];
  const usedRooms = (rooms.data ?? [])
    .filter((room) => allRows.some((row) => row.roomId === room._id))
    .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));

  if (settings.isPending || tasks.isPending || rooms.isPending || occurrences.isPending) {
    return (
      <p role="status" className="py-10 text-center text-muted-foreground">
        {t('app.loading')}
      </p>
    );
  }
  if (settings.isError || tasks.isError || rooms.isError || occurrences.isError) {
    return (
      <p role="alert" className="rounded-2xl bg-destructive/10 p-4 text-destructive">
        {t('app.error')}
      </p>
    );
  }

  return (
    <section className="flex flex-col gap-5">
      <PageHeader
        title={t('mobileTasks.title')}
        description={`${format('mobileTasks.rangeDescription', {
          from: compactDate(from),
          to: compactDate(to),
        })}${weeks === 1 && cycleWeek !== null ? ` · ${format('cycle.week', { week: cycleWeek })}` : ''}`}
        className="mb-0"
      />

      <div className="grid gap-3 rounded-2xl border bg-card p-3 shadow-sm sm:grid-cols-[minmax(0,1fr)_auto] sm:items-end">
        <fieldset className="grid gap-1.5">
          <legend className="text-sm font-semibold">{t('mobileTasks.rooms')}</legend>
          <div className="flex flex-wrap gap-2">
            {usedRooms.map((room) => {
              const checked = !hiddenRoomIds.has(room._id);
              return (
                <label
                  key={room._id}
                  className={cn(
                    'flex min-h-9 cursor-pointer items-center gap-2 rounded-lg border px-3 py-1.5 text-sm font-semibold transition-colors',
                    checked
                      ? 'border-primary/35 bg-primary/10 text-foreground'
                      : 'border-input bg-background text-muted-foreground',
                  )}
                >
                  <input
                    type="checkbox"
                    className="size-4 shrink-0 accent-primary"
                    checked={checked}
                    onChange={() =>
                      setHiddenRoomIds((current) => {
                        const next = new Set(current);
                        if (next.has(room._id)) next.delete(room._id);
                        else next.add(room._id);
                        return next;
                      })
                    }
                  />
                  <span>{room.name}</span>
                </label>
              );
            })}
          </div>
        </fieldset>
        <fieldset className="grid gap-1.5">
          <legend className="text-sm font-semibold">{t('mobileTasks.period')}</legend>
          <div className="grid grid-cols-3 rounded-lg bg-muted p-1" aria-label={t('mobileTasks.period')}>
            {WEEK_RANGES.map((range) => (
              <Button
                key={range}
                type="button"
                variant="ghost"
                size="sm"
                className={cn(
                  'h-8 rounded-md px-3 shadow-none',
                  weeks === range && 'bg-background text-foreground shadow-sm hover:bg-background',
                )}
                aria-pressed={weeks === range}
                onClick={() => setWeeks(range)}
              >
                {format(range === 1 ? 'mobileTasks.oneWeek' : 'mobileTasks.weeks', { count: range })}
              </Button>
            ))}
          </div>
        </fieldset>
      </div>

      {visible.mine.length + visible.unassigned.length === 0 ? (
        <div className="grid place-items-center gap-2 rounded-2xl border border-dashed px-4 py-10 text-center text-muted-foreground">
          <ListChecks className="size-7" aria-hidden="true" />
          <p>{t('mobileTasks.empty')}</p>
        </div>
      ) : (
        <div className="grid gap-5">
          <TaskTable title={t('mobileTasks.mine')} rows={visible.mine} />
          <TaskTable title={t('mobileTasks.unassigned')} rows={visible.unassigned} />
        </div>
      )}
    </section>
  );
}

function TaskTable({ title, rows }: { title: string; rows: TaskOverviewRow[] }) {
  if (rows.length === 0) return null;
  return (
    <section className="grid gap-2" aria-label={title}>
      <h2 className="px-1 text-lg font-extrabold">{title}</h2>
      <div className="overflow-hidden rounded-2xl border bg-card shadow-sm">
        <table className="w-full table-fixed border-collapse text-left text-sm">
          <thead className="bg-muted/70 text-xs text-muted-foreground">
            <tr>
              <th scope="col" className="w-[22%] px-1.5 py-2.5 font-semibold sm:px-2">
                {t('tasks.field.room')}
              </th>
              <th scope="col" className="w-[26%] px-2 py-2.5 font-semibold sm:px-3">
                {t('mobileTasks.task')}
              </th>
              <th scope="col" className="w-[52%] px-1.5 py-2.5 font-semibold sm:px-2">
                {t('mobileTasks.dates')}
              </th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr
                key={`${row.taskId}:${row.roomId ?? row.roomName}`}
                className="border-t align-top first:border-t-0"
              >
                <td className="px-1.5 py-3 text-muted-foreground break-words sm:px-2">{row.roomName}</td>
                <th scope="row" className="px-2 py-3 font-semibold break-words sm:px-3">
                  {row.taskName}
                </th>
                <td className="px-1.5 py-3 leading-relaxed text-muted-foreground break-words sm:px-2">
                  {row.dates.map(datedWeekday).join(', ')}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

import type { IntervalRow, StatsGroupBy, UserWorkload, WorkloadCycle } from '@huishoudplanner/shared';
import { CalendarClock, ChartColumnBig, CircleCheck, Clock, Hourglass, ListChecks, Scale, Trash2, TrendingUp } from 'lucide-react';
import { useId, useState, type ReactNode } from 'react';
import { EmptyState } from '@/components/EmptyState';
import { NativeSelect } from '@/components/NativeSelect';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Label } from '@/components/ui/label';
import { panelTabsListClass, panelTabsTriggerClass, Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { cn } from '@/lib/utils';
import { useRooms, useTasks, useUsers } from '../../api/queries.ts';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { useCompletion, useDeviations, useIntervals, useResetStatistics, useWorkload, type StatsPeriod } from './api.ts';
import { statsTableClass } from './ChartFrame.tsx';
import { FairnessBars, type FairnessRow } from './FairnessBars.tsx';
import { formatDays, formatFactor, formatMinutes, formatNumber, formatPercent, MAX_SERIES } from './scale.ts';
import { TrendLines, type TrendSeries } from './TrendLines.tsx';

const WEEK_PERIODS = [1, 2, 3];
const CYCLE_PERIODS = [1, 2, 4, 8, 13];
const GROUP_BY: StatsGroupBy[] = ['task', 'room', 'user'];

/** Deviation thresholds for the interval report ("wensdenken"). */
const LESS_OFTEN = 1.25;
const MORE_OFTEN = 0.8;

function dayMonth(dayKey: string): string {
  const [, m, d] = dayKey.split('-');
  return `${d}-${m}`;
}

function intervalVerdict(row: IntervalRow): { key: MessageKey; warn: boolean } {
  if (row.deviation === null) return { key: 'stats.intervals.tooFew', warn: false };
  if (row.deviation >= LESS_OFTEN) return { key: 'stats.intervals.lessOften', warn: true };
  if (row.deviation <= MORE_OFTEN) return { key: 'stats.intervals.moreOften', warn: false };
  return { key: 'stats.intervals.asIntended', warn: false };
}

function deviationVerdict(row: { completions: number; averagePlanningShiftDays: number; averageCompletionDelayDays: number }): MessageKey {
  if (row.completions < 2) return 'stats.deviations.tooFew';
  if (Math.abs(row.averagePlanningShiftDays) >= 0.75) return 'stats.deviations.replan';
  if (row.averageCompletionDelayDays >= 0.75) return 'stats.deviations.planLater';
  if (row.averageCompletionDelayDays <= -0.75) return 'stats.deviations.planEarlier';
  return 'stats.deviations.onTime';
}

function signedDays(value: number): string {
  const sign = value > 0 ? '+' : '';
  return `${sign}${formatDays(value)}`;
}

const sectionCardClass = 'flex flex-col gap-5 rounded-2xl border bg-card p-6 text-card-foreground shadow-sm';

/** Icon + h2 + explainer at the top of a stats card. */
function SectionHeader({ id, icon, title, explainer }: { id: string; icon: ReactNode; title: string; explainer?: string }) {
  return (
    <div className="flex items-start gap-3">
      <div className="grid size-10 shrink-0 place-items-center rounded-xl bg-accent text-accent-foreground [&_svg]:size-5">
        {icon}
      </div>
      <div className="min-w-0">
        <h2 id={id} className="text-lg leading-tight font-bold">
          {title}
        </h2>
        {explainer && <p className="mt-1 text-sm text-muted-foreground">{explainer}</p>}
      </div>
    </div>
  );
}

function KpiCard({ icon, label, value, tint }: { icon: ReactNode; label: string; value: string; tint: string }) {
  return (
    <div className="flex items-center gap-4 rounded-2xl border bg-card p-5 shadow-sm">
      <div className={cn('grid size-12 shrink-0 place-items-center rounded-2xl [&_svg]:size-6', tint)}>{icon}</div>
      <div className="min-w-0">
        <p className="text-sm font-semibold text-muted-foreground">{label}</p>
        <p className="text-2xl font-extrabold tracking-tight tabular-nums">{value}</p>
      </div>
    </div>
  );
}

export function StatsPage() {
  const idPrefix = useId();
  const [activeTab, setActiveTab] = useState('overview');
  const [period, setPeriod] = useState<StatsPeriod>({ unit: 'weeks', count: 1 });
  const [groupBy, setGroupBy] = useState<StatsGroupBy>('task');
  const [confirmReset, setConfirmReset] = useState(false);
  const [resetDone, setResetDone] = useState(false);
  const { profile } = useProfile();
  const workload = useWorkload(period);
  const completion = useCompletion(period, groupBy);
  const intervals = useIntervals(period);
  const deviations = useDeviations(period);
  const users = useUsers();
  const tasks = useTasks();
  const rooms = useRooms();
  const resetStatistics = useResetStatistics();

  const userName = (id: string | null) =>
    id === null ? t('tasks.anyone') : (users.data?.find((u) => u._id === id)?.name ?? t('tasks.unknownUser'));

  const taskWithRoom = (taskId: string | null, taskName: string) => {
    const task = tasks.data?.find((item) => item._id === taskId);
    const room = rooms.data?.find((item) => item._id === task?.roomId);
    return (
      <span className="grid gap-0.5">
        <span>{taskName}</span>
        {room && <span className="text-xs font-normal text-muted-foreground">{room.name}</span>}
      </span>
    );
  };

  const filters = (
    <div className="flex flex-wrap items-end gap-3" role="group" aria-label={t('stats.filters')}>
      <div className="flex w-48 flex-col gap-2">
        <Label htmlFor={`${idPrefix}-period`}>{t('stats.period')}</Label>
        <NativeSelect
          id={`${idPrefix}-period`}
          value={`${period.unit}:${period.count}`}
          onChange={(e) => {
            const [unit, count] = e.target.value.split(':');
            setPeriod({ unit: unit as StatsPeriod['unit'], count: Number(count) });
          }}
        >
          <optgroup label={t('stats.period.weeks')}>
            {WEEK_PERIODS.map((n) => (
              <option key={`weeks-${n}`} value={`weeks:${n}`}>
                {n === 1 ? t('stats.period.one') : format('stats.period.many', { n })}
              </option>
            ))}
          </optgroup>
          <optgroup label={t('stats.period.cycles')}>
            {CYCLE_PERIODS.map((n) => (
              <option key={`cycles-${n}`} value={`cycles:${n}`}>
                {n === 1 ? t('stats.period.cycleOne') : format('stats.period.cycleMany', { n })}
              </option>
            ))}
          </optgroup>
        </NativeSelect>
      </div>
      {profile?.role === 'admin' && (
        <Button
          type="button"
          variant="outline"
          className="text-destructive hover:text-destructive"
          disabled={resetStatistics.isPending}
          onClick={() => setConfirmReset(true)}
        >
          <Trash2 aria-hidden="true" />
          {t('stats.reset')}
        </Button>
      )}
    </div>
  );

  if (workload.isPending) {
    return (
      <section>
        <PageHeader title={t('nav.stats')} actions={filters} />
        <p role="status" className="text-muted-foreground">
          {t('app.loading')}
        </p>
      </section>
    );
  }
  if (workload.isError) {
    return (
      <section>
        <PageHeader title={t('nav.stats')} actions={filters} />
        <p role="alert" className="rounded-xl bg-destructive/10 p-4 text-destructive">
          {t('app.error')}
        </p>
      </section>
    );
  }

  const cycleList: WorkloadCycle[] = workload.data.cycles;
  const refreshing = workload.isFetching || completion.isFetching || intervals.isFetching || deviations.isFetching;

  // People in a fixed order (creation order from the users list), so colors follow the person.
  const orderedUserIds = [
    ...(users.data ?? []).map((u) => u._id).filter((id) => cycleList.some((c) => c.users.some((u) => u.userId === id))),
    ...[...new Set(cycleList.flatMap((c) => c.users.map((u) => u.userId)))].filter((id) => !(users.data ?? []).some((u) => u._id === id)),
  ];
  const visibleIds = orderedUserIds.slice(0, MAX_SERIES);
  const find = (list: UserWorkload[], id: string) => list.find((u) => u.userId === id);

  const fairness: FairnessRow[] = visibleIds.map((id) => ({
    userId: id,
    name: userName(id),
    planned: cycleList.reduce((sum, c) => sum + (find(c.users, id)?.plannedMinutes ?? 0), 0),
    done: cycleList.reduce((sum, c) => sum + (find(c.users, id)?.doneMinutes ?? 0), 0),
  }));

  const xLabels = cycleList.map((c) => format('stats.cycleLabel', { n: c.index + 1, date: dayMonth(c.startDate) }));
  const trend: TrendSeries[] = visibleIds.map((id, slot) => ({
    id,
    name: userName(id),
    slot,
    values: cycleList.map((c) => find(c.users, id)?.doneMinutes ?? 0),
    secondary: cycleList.map((c) => find(c.users, id)?.plannedMinutes ?? 0),
  }));
  const periodText = period.unit === 'weeks'
    ? period.count === 1 ? t('stats.period.one') : format('stats.period.many', { n: period.count })
    : period.count === 1 ? t('stats.period.cycleOne') : format('stats.period.cycleMany', { n: period.count });

  // Summary figures for the KPI cards (display only).
  const totalPlanned = cycleList.reduce((sum, c) => sum + c.users.reduce((s, u) => s + u.plannedMinutes, 0), 0);
  const totalDone = cycleList.reduce((sum, c) => sum + c.users.reduce((s, u) => s + u.doneMinutes, 0), 0);
  const totalUnassigned = cycleList.reduce((sum, c) => sum + c.unassignedPlannedMinutes, 0);

  return (
    <section
      className={cn(
        '[&_figure]:transition-opacity [&_table]:transition-opacity',
        refreshing && '[&_figure]:opacity-60 [&_table]:opacity-60',
      )}
    >
      <PageHeader title={t('nav.stats')} description={periodText} actions={filters} />

      <Dialog open={confirmReset} onOpenChange={setConfirmReset}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('stats.resetConfirmTitle')}</DialogTitle>
            <DialogDescription>{t('stats.resetConfirmBody')}</DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button type="button" variant="ghost" onClick={() => setConfirmReset(false)}>{t('common.cancel')}</Button>
            <Button
              type="button"
              variant="destructive"
              disabled={resetStatistics.isPending}
              onClick={() =>
                resetStatistics.mutate(undefined, {
                  onSuccess: () => {
                    setConfirmReset(false);
                    setResetDone(true);
                  },
                })
              }
            >
              <Trash2 aria-hidden="true" />
              {t('stats.resetConfirm')}
            </Button>
          </DialogFooter>
          {resetStatistics.isError && <p role="alert" className="text-sm font-semibold text-destructive">{t('stats.resetError')}</p>}
        </DialogContent>
      </Dialog>

      {resetDone && <p role="status" className="mb-6 rounded-xl bg-success/15 px-4 py-3 font-semibold text-success">{t('stats.resetDone')}</p>}

      <Tabs value={activeTab} onValueChange={setActiveTab} className="gap-6">
        <TabsList className={cn(panelTabsListClass, 'grid max-w-4xl grid-cols-6 gap-1 overflow-hidden p-1.5')} aria-label={t('stats.tabs')}>
          <TabsTrigger aria-label={t('stats.tab.overview')} className={cn(panelTabsTriggerClass, 'w-full min-w-0 px-2')} value="overview"><ChartColumnBig aria-hidden="true" /><span className="hidden md:inline">{t('stats.tab.overview')}</span></TabsTrigger>
          <TabsTrigger aria-label={t('stats.fairness')} className={cn(panelTabsTriggerClass, 'w-full min-w-0 px-2')} value="fairness"><Scale aria-hidden="true" /><span className="hidden md:inline">{t('stats.tab.fairness')}</span></TabsTrigger>
          <TabsTrigger aria-label={t('stats.trend')} className={cn(panelTabsTriggerClass, 'w-full min-w-0 px-2')} value="trend"><TrendingUp aria-hidden="true" /><span className="hidden md:inline">{t('stats.tab.trend')}</span></TabsTrigger>
          <TabsTrigger aria-label={t('stats.completion')} className={cn(panelTabsTriggerClass, 'w-full min-w-0 px-2')} value="completion"><CircleCheck aria-hidden="true" /><span className="hidden md:inline">{t('stats.tab.completion')}</span></TabsTrigger>
          <TabsTrigger aria-label={t('stats.intervals')} className={cn(panelTabsTriggerClass, 'w-full min-w-0 px-2')} value="intervals"><CalendarClock aria-hidden="true" /><span className="hidden md:inline">{t('stats.tab.intervals')}</span></TabsTrigger>
          <TabsTrigger aria-label={t('stats.deviations')} className={cn(panelTabsTriggerClass, 'w-full min-w-0 px-2')} value="deviations"><Clock aria-hidden="true" /><span className="hidden md:inline">{t('stats.tab.deviations')}</span></TabsTrigger>
        </TabsList>

        <TabsContent value="overview">
          {cycleList.length === 0 ? (
            <EmptyState icon={<ChartColumnBig className="size-6" aria-hidden="true" />}>
              {t('stats.empty')}
            </EmptyState>
          ) : (
            <div className="grid gap-4 sm:grid-cols-3">
              <KpiCard
                icon={<ListChecks aria-hidden="true" />}
                label={t('stats.planned')}
                value={formatMinutes(totalPlanned)}
                tint="bg-primary/10 text-primary"
              />
              <KpiCard
                icon={<CircleCheck aria-hidden="true" />}
                label={t('stats.done')}
                value={formatMinutes(totalDone)}
                tint="bg-accent text-accent-foreground"
              />
              <KpiCard
                icon={<Hourglass aria-hidden="true" />}
                label={t('stats.unassigned')}
                value={formatMinutes(totalUnassigned)}
                tint="bg-warning/25 text-warning-foreground"
              />
            </div>
          )}
        </TabsContent>

        <TabsContent value="fairness">
          {cycleList.length === 0 ? (
            <EmptyState icon={<Scale className="size-6" aria-hidden="true" />}>
              {t('stats.empty')}
            </EmptyState>
          ) : (
          <section className={sectionCardClass} aria-labelledby={`${idPrefix}-fair`}>
            <SectionHeader
              id={`${idPrefix}-fair`}
              icon={<Scale aria-hidden="true" />}
              title={t('stats.fairness')}
              explainer={t('stats.fairness.explainer')}
            />
            <FairnessBars title={t('stats.fairness.chart')} caption={format('stats.caption.minutes', { period: periodText })} rows={fairness} />

            <div className="overflow-x-auto rounded-xl border p-4">
              <table className={statsTableClass}>
                <caption>{t('stats.perWeek')}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t('stats.week')}</th>
                    {visibleIds.map((id) => (
                      <th key={id} scope="col">
                        {userName(id)}
                      </th>
                    ))}
                    <th scope="col">{t('stats.unassigned')}</th>
                  </tr>
                </thead>
                <tbody>
                  {cycleList.flatMap((c) =>
                    c.weeks.map((w) => (
                      <tr key={`${c.index}-${w.weekIndex}`}>
                        <th scope="row" className="whitespace-nowrap">
                          {format('stats.weekLabel', { cycle: c.index + 1, week: w.weekIndex + 1, date: dayMonth(w.startDate) })}
                        </th>
                        {visibleIds.map((id) => {
                          const u = find(w.users, id);
                          return (
                            <td key={id}>
                              {format('stats.plannedDone', { planned: formatNumber(u?.plannedMinutes ?? 0), done: formatNumber(u?.doneMinutes ?? 0) })}
                            </td>
                          );
                        })}
                        <td className={cn(w.unassignedPlannedMinutes > 0 && 'font-semibold text-warning-foreground')}>
                          {formatMinutes(w.unassignedPlannedMinutes)}
                        </td>
                      </tr>
                    )),
                  )}
                </tbody>
              </table>
            </div>
          </section>
          )}
        </TabsContent>

        <TabsContent value="trend">
          {cycleList.length === 0 ? (
            <EmptyState icon={<TrendingUp className="size-6" aria-hidden="true" />}>
              {t('stats.empty')}
            </EmptyState>
          ) : (
          <section className={sectionCardClass} aria-labelledby={`${idPrefix}-trend`}>
            <SectionHeader id={`${idPrefix}-trend`} icon={<TrendingUp aria-hidden="true" />} title={t('stats.trend')} />
            <TrendLines
              title={t('stats.trend.chart')}
              caption={t('stats.trend.caption')}
              xLabels={xLabels}
              series={trend}
              secondaryLabel={t('stats.planned')}
            />
          </section>
          )}
        </TabsContent>

        <TabsContent value="completion">
        <section className={sectionCardClass} aria-labelledby={`${idPrefix}-completion`}>
          <SectionHeader
            id={`${idPrefix}-completion`}
            icon={<CircleCheck aria-hidden="true" />}
            title={t('stats.completion')}
            explainer={t('stats.completion.explainer')}
          />
          <div className="flex w-44 flex-col gap-2">
            <Label htmlFor={`${idPrefix}-group`}>{t('stats.completion.groupBy')}</Label>
            <NativeSelect id={`${idPrefix}-group`} value={groupBy} onChange={(e) => setGroupBy(e.target.value as StatsGroupBy)}>
              {GROUP_BY.map((g) => (
                <option key={g} value={g}>
                  {t(`stats.groupBy.${g}` as MessageKey)}
                </option>
              ))}
            </NativeSelect>
          </div>
          {completion.data && completion.data.rows.length > 0 ? (
            <div className="overflow-x-auto">
              <table className={statsTableClass}>
                <thead>
                  <tr>
                    <th scope="col">{t(`stats.groupBy.${completion.data.groupBy}` as MessageKey)}</th>
                    <th scope="col">{t('stats.completion.done')}</th>
                    <th scope="col">{t('stats.completion.skipped')}</th>
                    <th scope="col">{t('stats.completion.missed')}</th>
                    <th scope="col">{t('stats.completion.rate')}</th>
                  </tr>
                </thead>
                <tbody>
                  {completion.data.rows.map((row) => (
                    <tr key={row.key ?? 'none'}>
                      <th scope="row">
                        {row.key === null
                          ? t('tasks.anyone')
                          : completion.data.groupBy === 'task'
                            ? taskWithRoom(row.key, row.name)
                            : row.name}
                      </th>
                      <td>{row.done}</td>
                      <td>{row.skipped}</td>
                      <td>{row.missed}</td>
                      <td className="font-bold text-primary">{formatPercent(row.rate)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <p className="rounded-xl border border-dashed px-4 py-3 text-sm text-muted-foreground">
              {completion.isPending ? t('app.loading') : t('stats.completion.none')}
            </p>
          )}
        </section>
        </TabsContent>

        <TabsContent value="intervals">
        <section className={sectionCardClass} aria-labelledby={`${idPrefix}-intervals`}>
          <SectionHeader
            id={`${idPrefix}-intervals`}
            icon={<Clock aria-hidden="true" />}
            title={t('stats.intervals')}
            explainer={t('stats.intervals.explainer')}
          />
          {intervals.data && intervals.data.rows.length > 0 ? (
            <div className="overflow-x-auto">
              <table className={statsTableClass}>
                <thead>
                  <tr>
                    <th scope="col">{t('stats.groupBy.task')}</th>
                    <th scope="col">{t('stats.intervals.intended')}</th>
                    <th scope="col">{t('stats.intervals.actual')}</th>
                    <th scope="col">{t('stats.intervals.verdict')}</th>
                  </tr>
                </thead>
                <tbody>
                  {intervals.data.rows.map((row) => {
                    const verdict = intervalVerdict(row);
                    return (
                      <tr key={row.taskId} className={cn(verdict.warn && 'bg-warning/15')}>
                        <th scope="row">{taskWithRoom(row.taskId, row.name)}</th>
                        <td>{format('stats.intervals.every', { days: row.periodDays })}</td>
                        <td>{row.averageDays === null ? '—' : formatDays(row.averageDays)}</td>
                        <td className={cn(verdict.warn ? 'font-bold text-warning-foreground' : 'text-muted-foreground')}>
                          {verdict.warn && <span aria-hidden="true">⚠ </span>}
                          {row.deviation === null
                            ? t(verdict.key)
                            : format(verdict.key, { factor: formatFactor(row.deviation) })}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          ) : (
            <p className="rounded-xl border border-dashed px-4 py-3 text-sm text-muted-foreground">
              {intervals.isPending ? t('app.loading') : t('stats.intervals.none')}
            </p>
          )}
        </section>
        </TabsContent>

        <TabsContent value="deviations">
        <section className={sectionCardClass} aria-labelledby={`${idPrefix}-deviations`}>
          <SectionHeader
            id={`${idPrefix}-deviations`}
            icon={<CalendarClock aria-hidden="true" />}
            title={t('stats.deviations')}
            explainer={t('stats.deviations.explainer')}
          />
          {deviations.data && deviations.data.rows.length > 0 ? (
            <div className="overflow-x-auto">
              <table className={statsTableClass}>
                <thead>
                  <tr>
                    <th scope="col">{t('stats.groupBy.task')}</th>
                    <th scope="col">{t('stats.deviations.measurements')}</th>
                    <th scope="col">{t('stats.deviations.planning')}</th>
                    <th scope="col">{t('stats.deviations.execution')}</th>
                    <th scope="col">{t('stats.deviations.advice')}</th>
                  </tr>
                </thead>
                <tbody>
                  {deviations.data.rows.map((row) => (
                    <tr key={row.taskId}>
                      <th scope="row">{taskWithRoom(row.taskId, row.name)}</th>
                      <td>{row.completions}</td>
                      <td>{signedDays(row.averagePlanningShiftDays)}</td>
                      <td>{signedDays(row.averageCompletionDelayDays)}</td>
                      <td className="text-muted-foreground">{t(deviationVerdict(row))}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <p className="rounded-xl border border-dashed px-4 py-3 text-sm text-muted-foreground">
              {deviations.isPending ? t('app.loading') : t('stats.deviations.none')}
            </p>
          )}
        </section>
        </TabsContent>
      </Tabs>
    </section>
  );
}

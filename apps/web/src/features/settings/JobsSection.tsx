import { useMutation, useQueryClient } from '@tanstack/react-query';
import { BellRing, CalendarSync, History, Play, Scale } from 'lucide-react';
import type { ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { api } from '../../api/index.ts';
import { format, t } from '../../i18n/nl.ts';
import { FormMessage, SettingsCardHeader, listRowClass, settingsCardClass } from './SettingsCard.tsx';

interface GenerationResult {
  removed: number;
  generated: { inserted: number }[];
  due: { due: number; overdue: number };
}

interface RecomputeResult {
  tasksDefaulted: number;
  snapshotsSet: number;
  created: number;
  updated: number;
  removed: number;
  bonusesCreated: number;
  bonusesRemoved: number;
}

interface MorningResult {
  status: 'disabled' | 'done' | 'error';
  sent: number;
  failed: number;
  quiet: number;
}

type AuditRetentionResult =
  | { status: 'disabled' }
  | { status: 'done'; cutoff: string; deleted: number };

function JobRow({
  icon,
  title,
  schedule,
  description,
  pending,
  result,
  failed,
  onRun,
}: {
  icon: ReactNode;
  title: string;
  schedule: string;
  description: string;
  pending: boolean;
  result?: string;
  failed: boolean;
  onRun: () => void;
}) {
  return (
    <div
      className={cn(
        listRowClass,
        'min-w-0 max-w-full flex-col flex-nowrap items-stretch sm:flex-row sm:flex-wrap sm:items-center',
      )}
    >
      <div className="grid size-10 shrink-0 place-items-center rounded-lg bg-muted text-muted-foreground [&_svg]:size-5">
        {icon}
      </div>
      <div className="min-w-0 max-w-full flex-1 [overflow-wrap:anywhere]">
        <div className="flex flex-wrap items-baseline gap-x-2">
          <h3 className="font-bold">{title}</h3>
          <span className="text-xs font-semibold text-muted-foreground">{schedule}</span>
        </div>
        <p className="mt-1 text-sm text-muted-foreground">{description}</p>
      </div>
      <Button type="button" variant="secondary" className="w-full sm:w-auto" disabled={pending} onClick={onRun}>
        <Play aria-hidden="true" />
        {pending ? t('settings.jobs.running') : t('settings.jobs.run')}
      </Button>
      {(result || failed) && (
        <div className="min-w-0 max-w-full basis-full [&>p]:min-w-0 [&>p]:[overflow-wrap:anywhere]">
          <FormMessage kind={failed ? 'alert' : 'status'}>
            {failed ? t('settings.jobs.error') : result}
          </FormMessage>
        </div>
      )}
    </div>
  );
}

export function JobsSection() {
  const queryClient = useQueryClient();
  const generation = useMutation({
    mutationFn: async () => (await api.post<GenerationResult>('/api/jobs/generation')).data,
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['occurrences'] }),
        queryClient.invalidateQueries({ queryKey: ['due'] }),
        queryClient.invalidateQueries({ queryKey: ['cycles'] }),
      ]);
    },
  });
  const recompute = useMutation({
    mutationFn: async () => (await api.post<RecomputeResult>('/api/points/recompute')).data,
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['points'] }),
        queryClient.invalidateQueries({ queryKey: ['badges'] }),
        queryClient.invalidateQueries({ queryKey: ['stats'] }),
      ]);
    },
  });
  const morning = useMutation({
    mutationFn: async () => (await api.post<MorningResult>('/api/jobs/morning-notify')).data,
  });
  const retention = useMutation({
    mutationFn: async () => (await api.post<AuditRetentionResult>('/api/jobs/audit-retention')).data,
  });

  const generated = generation.data?.generated.reduce((sum, cycle) => sum + cycle.inserted, 0) ?? 0;
  const generationResult = generation.data
    ? format('settings.jobs.generation.result', {
        removed: generation.data.removed,
        generated,
        due: generation.data.due.due,
        overdue: generation.data.due.overdue,
      })
    : undefined;
  const recomputed = recompute.data;
  const recomputeResult = recomputed
    ? recomputed.created + recomputed.updated + recomputed.removed + recomputed.bonusesCreated + recomputed.bonusesRemoved +
        recomputed.snapshotsSet + recomputed.tasksDefaulted ===
      0
      ? t('settings.jobs.recompute.unchanged')
      : format('settings.jobs.recompute.result', {
          created: recomputed.created + recomputed.bonusesCreated,
          updated: recomputed.updated + recomputed.snapshotsSet + recomputed.tasksDefaulted,
          removed: recomputed.removed + recomputed.bonusesRemoved,
        })
    : undefined;
  const morningResult = morning.data
    ? morning.data.status === 'disabled'
      ? t('settings.jobs.morning.disabled')
      : morning.data.status === 'error'
        ? t('settings.jobs.morning.error')
        : format('settings.jobs.morning.result', {
            sent: morning.data.sent,
            failed: morning.data.failed,
            quiet: morning.data.quiet,
          })
    : undefined;
  const retentionResult = retention.data
    ? retention.data.status === 'disabled'
      ? t('settings.jobs.retention.disabled')
      : format('settings.jobs.retention.result', { deleted: retention.data.deleted })
    : undefined;

  return (
    <section className={cn(settingsCardClass, 'min-w-0 max-w-full p-3 sm:p-6')} aria-labelledby="jobs-title">
      <SettingsCardHeader
        icon={<CalendarSync aria-hidden="true" />}
        titleId="jobs-title"
        title={t('settings.jobs.title')}
        description={<span className="[overflow-wrap:anywhere]">{t('settings.jobs.help')}</span>}
      />
      <div className="grid min-w-0 max-w-full gap-3">
        <JobRow
          icon={<CalendarSync aria-hidden="true" />}
          title={t('settings.jobs.generation.title')}
          schedule={t('settings.jobs.generation.schedule')}
          description={t('settings.jobs.generation.help')}
          pending={generation.isPending}
          result={generationResult}
          failed={generation.isError}
          onRun={() => generation.mutate()}
        />
        <JobRow
          icon={<Scale aria-hidden="true" />}
          title={t('settings.jobs.recompute.title')}
          schedule={t('settings.jobs.recompute.schedule')}
          description={t('settings.jobs.recompute.help')}
          pending={recompute.isPending}
          result={recomputeResult}
          failed={recompute.isError}
          onRun={() => recompute.mutate()}
        />
        <JobRow
          icon={<History aria-hidden="true" />}
          title={t('settings.jobs.retention.title')}
          schedule={t('settings.jobs.retention.schedule')}
          description={t('settings.jobs.retention.help')}
          pending={retention.isPending}
          result={retentionResult}
          failed={retention.isError}
          onRun={() => retention.mutate()}
        />
        <JobRow
          icon={<BellRing aria-hidden="true" />}
          title={t('settings.jobs.morning.title')}
          schedule={t('settings.jobs.morning.schedule')}
          description={t('settings.jobs.morning.help')}
          pending={morning.isPending}
          result={morningResult}
          failed={morning.isError}
          onRun={() => morning.mutate()}
        />
      </div>
    </section>
  );
}

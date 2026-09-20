import { useMutation, useQueryClient } from '@tanstack/react-query';
import { BellRing, CalendarSync, History, Play } from 'lucide-react';
import type { ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { api } from '../../api/index.ts';
import { format, t } from '../../i18n/nl.ts';
import { FormMessage, SettingsCardHeader, listRowClass, settingsCardClass } from './SettingsCard.tsx';

interface NightlyResult {
  removed: number;
  generated: { inserted: number }[];
  due: { due: number; overdue: number };
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
  const nightly = useMutation({
    mutationFn: async () => (await api.post<NightlyResult>('/api/jobs/nightly')).data,
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['occurrences'] }),
        queryClient.invalidateQueries({ queryKey: ['due'] }),
        queryClient.invalidateQueries({ queryKey: ['cycles'] }),
      ]);
    },
  });
  const morning = useMutation({
    mutationFn: async () => (await api.post<MorningResult>('/api/jobs/morning-notify')).data,
  });
  const retention = useMutation({
    mutationFn: async () => (await api.post<AuditRetentionResult>('/api/jobs/audit-retention')).data,
  });

  const generated = nightly.data?.generated.reduce((sum, cycle) => sum + cycle.inserted, 0) ?? 0;
  const nightlyResult = nightly.data
    ? format('settings.jobs.nightly.result', {
      removed: nightly.data.removed,
        generated,
        due: nightly.data.due.due,
        overdue: nightly.data.due.overdue,
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
          title={t('settings.jobs.nightly.title')}
          schedule={t('settings.jobs.nightly.schedule')}
          description={t('settings.jobs.nightly.help')}
          pending={nightly.isPending}
          result={nightlyResult}
          failed={nightly.isError}
          onRun={() => nightly.mutate()}
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

import type { OccurrenceView } from '@huishoudplanner/shared';
import { Sun, TriangleAlert, Undo2 } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { EmptyState } from '@/components/EmptyState';
import { PageHeader } from '@/components/PageHeader';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { useRooms, useSettings, useTasks } from '../../api/queries.ts';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { PromoteBanner } from '../promote/PromoteBanner.tsx';
import {
  occurrenceKeys,
  useOccurrenceAction,
  useOccurrences,
  type OccurrenceAction,
} from './api.ts';
import { OccurrenceItem } from './OccurrenceItem.tsx';
import {
  addDaysKey,
  dayKeyInZone,
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
  const from = addDaysKey(todayKey, -OVERDUE_LOOKBACK_DAYS);
  const occurrences = useOccurrences(from, todayKey, settings.isSuccess);
  const profileId = profile?._id ?? '';
  const action = useOccurrenceAction(occurrenceKeys.range(from, todayKey), { profileId, todayKey });

  const [snackbar, setSnackbar] = useState<{ id: string; task: string } | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (!snackbar) return;
    const timer = setTimeout(() => setSnackbar(null), UNDO_SNACKBAR_MS);
    return () => clearTimeout(timer);
  }, [snackbar]);

  const roomByTask = useMemo(() => {
    const roomNames = new Map((rooms.data ?? []).map((r) => [r._id, r.name]));
    return new Map((tasks.data ?? []).map((task) => [task._id, roomNames.get(task.roomId)]));
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

  const run = (next: OccurrenceAction, occ: OccurrenceView) => {
    setFailed(false);
    if (next.kind === 'complete') setSnackbar({ id: occ._id, task: occ.taskNameSnapshot });
    if (next.kind === 'uncomplete') setSnackbar(null);
    action.mutate(next, {
      onError: () => {
        setFailed(true);
        setSnackbar(null);
      },
    });
  };

  const groups = groupToday(occurrences.data, profileId, todayKey);
  const nothingOpen =
    groups.mine.length + groups.unclaimed.length + groups.others.length + groups.overdue.length ===
    0;

  return (
    <section className="flex flex-col gap-6">
      <PageHeader title={t('nav.today')} className="mb-0" />
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
                {t(title)}
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
                  key={occ._id}
                  occurrence={occ}
                  roomName={occ.roomNameSnapshot ?? roomByTask.get(occ.taskId)}
                  users={activeUsers}
                  profileId={profileId}
                  onComplete={(completedBy) =>
                    run({ id: occ._id, kind: 'complete', completedBy }, occ)
                  }
                  onUncomplete={() => run({ id: occ._id, kind: 'uncomplete' }, occ)}
                  onSkip={(reason) => run({ id: occ._id, kind: 'skip', reason }, occ)}
                  onClaim={() => run({ id: occ._id, kind: 'claim' }, occ)}
                />
              ))}
            </ul>
          </section>
        ),
      )}

      {snackbar && (
        <div
          className="snackbar fixed inset-x-4 bottom-24 z-30 mx-auto flex max-w-md items-center justify-between gap-3 rounded-full bg-foreground py-1.5 pr-1.5 pl-5 text-background shadow-lg"
          role="status"
        >
          <span className="min-w-0 truncate text-sm font-semibold">
            {format('today.snackbar', { task: snackbar.task })}
          </span>
          <Button
            type="button"
            variant="ghost"
            className="h-11 shrink-0 rounded-full bg-background/15 px-4 font-bold text-background hover:bg-background/25 hover:text-background"
            onClick={() => {
              const occ = occurrences.data.find((o) => o._id === snackbar.id);
              if (occ) run({ id: occ._id, kind: 'uncomplete' }, occ);
            }}
          >
            <Undo2 aria-hidden="true" />
            {t('today.undo')}
          </Button>
        </div>
      )}
    </section>
  );
}

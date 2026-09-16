import type { AuditEntity, AuditEntry } from '@huishoudplanner/shared';
import { ArrowLeft, Bot, Clock, Filter, History, Sparkles, Trash2, UserRound } from 'lucide-react';
import { useId, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { EmptyState } from '@/components/EmptyState';
import { NativeSelect } from '@/components/NativeSelect';
import { PageHeader } from '@/components/PageHeader';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { useRooms, useSettings, useTasks, useUsers } from '../../api/queries.ts';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import { getLocale } from '../../i18n/runtime.ts';
import { Avatar } from '../../identity/Avatar.tsx';
import { usePlans } from '../planner/api.ts';
import { useAuditFeed, useClearAudit, type AuditFilters } from './api.ts';
import { collectOccurrenceNames, describeEntry, entityName, SYSTEM_ACTOR_ID, type NameLookup } from './describe.ts';

const ENTITY_TYPES: AuditEntity[] = ['task', 'cyclePlan', 'occurrence', 'user', 'room', 'settings', 'cycle', 'import'];

/** Browser-local day boundaries for the date filter. */
function dayStartIso(day: string): string {
  return new Date(`${day}T00:00:00`).toISOString();
}
function dayEndIso(day: string): string {
  return new Date(`${day}T23:59:59.999`).toISOString();
}

/**
 * Global feed with filters on actor, entity type and date range; with
 * `?entity=…&entityId=…` it becomes the history panel of that one entity.
 */
export function HistoryPage() {
  const idPrefix = useId();
  const [params, setParams] = useSearchParams();
  const entity = (params.get('entity') ?? '') as AuditEntity | '';
  const entityId = params.get('entityId') ?? '';
  const actorId = params.get('actorId') ?? '';
  const fromDay = params.get('from') ?? '';
  const toDay = params.get('to') ?? '';
  const panelMode = Boolean(entity && entityId);
  const [confirmClear, setConfirmClear] = useState(false);

  const filters: AuditFilters = {
    entity,
    entityId,
    actorId,
    ...(fromDay ? { from: dayStartIso(fromDay) } : {}),
    ...(toDay ? { to: dayEndIso(toDay) } : {}),
  };
  const feed = useAuditFeed(filters);
  const users = useUsers();
  const tasks = useTasks();
  const rooms = useRooms();
  const plans = usePlans();
  const settings = useSettings();
  const clearAudit = useClearAudit();

  const entries: AuditEntry[] = useMemo(() => feed.data?.pages.flatMap((p) => p.items) ?? [], [feed.data]);

  const names: NameLookup = useMemo(
    () => ({
      users: new Map((users.data ?? []).map((u) => [u._id, u.name])),
      tasks: new Map((tasks.data ?? []).map((task) => [task._id, task.name])),
      rooms: new Map((rooms.data ?? []).map((r) => [r._id, r.name])),
      plans: new Map((plans.data ?? []).map((p) => [p._id, p.name])),
      intervals: new Map((settings.data?.intervals ?? []).map((i) => [i.key, i.label])),
      occurrences: collectOccurrenceNames(entries),
      timezone: settings.data?.timezone ?? 'Europe/Amsterdam',
    }),
    [users.data, tasks.data, rooms.data, plans.data, settings.data, entries],
  );

  const setFilter = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next);
  };

  const dateTime = new Intl.DateTimeFormat(getLocale(), {
    timeZone: names.timezone,
    weekday: 'short',
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });

  const title =
    panelMode && entries[0]
      ? format('history.titleFor', { name: entityName(entries[0], names) })
      : t('history.title');

  const actorAvatar = (entry: AuditEntry) => {
    if (entry.actorId === SYSTEM_ACTOR_ID) {
      return (
        <span className="grid size-9 place-items-center rounded-full bg-secondary text-secondary-foreground shadow-sm ring-2 ring-card">
          <Bot className="size-5" aria-hidden="true" />
        </span>
      );
    }
    const actor = users.data?.find((u) => u._id === entry.actorId);
    if (actor) return <Avatar name={actor.name} color={actor.color} size="md" />;
    return (
      <span className="grid size-9 place-items-center rounded-full bg-muted text-muted-foreground shadow-sm ring-2 ring-card">
        <UserRound className="size-5" aria-hidden="true" />
      </span>
    );
  };

  return (
    <section>
      <PageHeader
        title={title}
        actions={
          panelMode ? (
            <Button asChild variant="outline">
              <Link to="/history">
                <ArrowLeft aria-hidden="true" />
                {t('history.all')}
              </Link>
            </Button>
          ) : (
            <Button
              type="button"
              variant="outline"
              className="text-destructive hover:text-destructive"
              disabled={entries.length === 0 || clearAudit.isPending}
              onClick={() => setConfirmClear(true)}
            >
              <Trash2 aria-hidden="true" />
              {t('history.clear')}
            </Button>
          )
        }
      />

      <Dialog open={confirmClear} onOpenChange={setConfirmClear}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('history.clearConfirmTitle')}</DialogTitle>
            <DialogDescription>{t('history.clearConfirmBody')}</DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button type="button" variant="ghost" onClick={() => setConfirmClear(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              type="button"
              variant="destructive"
              disabled={clearAudit.isPending}
              onClick={() =>
                clearAudit.mutate(undefined, { onSuccess: () => setConfirmClear(false) })
              }
            >
              <Trash2 aria-hidden="true" />
              {t('history.clearConfirm')}
            </Button>
          </DialogFooter>
          {clearAudit.isError && (
            <p role="alert" className="text-sm font-semibold text-destructive">
              {t('history.clearError')}
            </p>
          )}
        </DialogContent>
      </Dialog>

      {!panelMode && (
        <div
          className="mb-6 grid items-end gap-4 rounded-2xl border bg-card p-5 shadow-sm sm:grid-cols-2 lg:grid-cols-[auto_1fr_1fr_1fr_1fr]"
          role="search"
        >
          <div className="hidden size-10 place-items-center rounded-xl bg-accent text-accent-foreground lg:grid">
            <Filter className="size-5" aria-hidden="true" />
          </div>
          <div className="flex flex-col gap-2">
            <Label htmlFor={`${idPrefix}-actor`}>{t('history.filter.actor')}</Label>
            <NativeSelect id={`${idPrefix}-actor`} value={actorId} onChange={(e) => setFilter('actorId', e.target.value)}>
              <option value="">{t('history.filter.everyone')}</option>
              {(users.data ?? []).map((user) => (
                <option key={user._id} value={user._id}>
                  {user.name}
                </option>
              ))}
              <option value={SYSTEM_ACTOR_ID}>{t('history.system')}</option>
            </NativeSelect>
          </div>
          <div className="flex flex-col gap-2">
            <Label htmlFor={`${idPrefix}-entity`}>{t('history.filter.entity')}</Label>
            <NativeSelect id={`${idPrefix}-entity`} value={entity} onChange={(e) => setFilter('entity', e.target.value)}>
              <option value="">{t('history.filter.allEntities')}</option>
              {ENTITY_TYPES.map((type) => (
                <option key={type} value={type}>
                  {t(`history.entities.${type}` as MessageKey)}
                </option>
              ))}
            </NativeSelect>
          </div>
          <div className="flex flex-col gap-2">
            <Label htmlFor={`${idPrefix}-from`}>{t('history.filter.from')}</Label>
            <Input
              id={`${idPrefix}-from`}
              type="date"
              className="h-10 bg-card"
              value={fromDay}
              onChange={(e) => setFilter('from', e.target.value)}
            />
          </div>
          <div className="flex flex-col gap-2">
            <Label htmlFor={`${idPrefix}-to`}>{t('history.filter.to')}</Label>
            <Input
              id={`${idPrefix}-to`}
              type="date"
              className="h-10 bg-card"
              value={toDay}
              onChange={(e) => setFilter('to', e.target.value)}
            />
          </div>
        </div>
      )}

      {feed.isPending ? (
        <p role="status" className="text-muted-foreground">
          {t('app.loading')}
        </p>
      ) : feed.isError ? (
        <p role="alert" className="rounded-xl bg-destructive/10 p-4 text-destructive">
          {t('app.error')}
        </p>
      ) : entries.length === 0 ? (
        <EmptyState icon={<History className="size-6" aria-hidden="true" />}>{t('history.empty')}</EmptyState>
      ) : (
        <ol className="history-list ml-4 flex flex-col gap-3 border-l-2 border-dashed border-border pl-8">
          {entries.map((entry) => (
            <li key={entry._id} className="relative flex items-start gap-4 rounded-2xl border bg-card px-5 py-4 shadow-sm">
              <div className="absolute top-3.5 -left-[51px]">{actorAvatar(entry)}</div>
              <div className="flex min-w-0 flex-1 flex-col gap-1.5">
                <div className="flex flex-col gap-0.5 leading-snug">
                  {describeEntry(entry, names).map((line, i) => (
                    <p key={i} className={i === 0 ? 'font-semibold' : 'text-sm text-muted-foreground'}>
                      {line}
                    </p>
                  ))}
                </div>
                <time dateTime={entry.at} className="flex items-center gap-1.5 text-xs text-muted-foreground tabular-nums">
                  <Clock className="size-3.5" aria-hidden="true" />
                  {dateTime.format(new Date(entry.at))}
                </time>
              </div>
              {entry.source === 'ai' && (
                <Badge variant="secondary" className="shrink-0 bg-primary/10 font-semibold text-primary">
                  <Sparkles aria-hidden="true" />
                  {t('history.viaAi')}
                </Badge>
              )}
            </li>
          ))}
        </ol>
      )}

      {feed.hasNextPage && (
        <div className="mt-6 flex justify-center">
          <Button type="button" variant="outline" onClick={() => void feed.fetchNextPage()} disabled={feed.isFetchingNextPage}>
            {t('history.loadMore')}
          </Button>
        </div>
      )}
    </section>
  );
}

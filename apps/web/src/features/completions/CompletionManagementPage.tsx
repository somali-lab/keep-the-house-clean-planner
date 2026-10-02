import type { OccurrenceView } from '@huishoudplanner/shared';
import { CheckCircle2, Filter, Pencil, Trash2 } from 'lucide-react';
import { useId, useMemo, useState } from 'react';
import { EmptyState } from '@/components/EmptyState';
import { NativeSelect } from '@/components/NativeSelect';
import { PageHeader } from '@/components/PageHeader';
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
import { useSettings, useUsers } from '../../api/queries.ts';
import { format, t } from '../../i18n/nl.ts';
import { getLocale } from '../../i18n/runtime.ts';
import { useProfile } from '../../identity/index.ts';
import { usePersistedFilter } from '../../hooks/usePersistedFilter.ts';
import { addDaysKey, dayKeyInZone } from '../today/todayModel.ts';
import { useCompletionRecords, useDeleteCompletion, useEditCompletion } from './api.ts';

interface EditState {
  occurrence: OccurrenceView;
  date: string;
  completedAt: string;
  completedBy: string;
}

function localDateTimeInput(iso: string): string {
  const value = new Date(iso);
  const local = new Date(value.getTime() - value.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}

export function CompletionManagementPage({ now }: { now?: Date }) {
  const idPrefix = useId();
  const settings = useSettings();
  const users = useUsers();
  const timezone = settings.data?.timezone ?? 'Europe/Amsterdam';
  const today = dayKeyInZone(now ?? new Date(), timezone);
  const { profile } = useProfile();
  const [from, setFrom, resetFrom] = usePersistedFilter('completions.from', profile?._id ?? null, addDaysKey(today, -90));
  const [to, setTo, resetTo] = usePersistedFilter('completions.to', profile?._id ?? null, today);
  const completions = useCompletionRecords(from, to);
  const editCompletion = useEditCompletion();
  const deleteCompletion = useDeleteCompletion();
  const [edit, setEdit] = useState<EditState | null>(null);
  const [remove, setRemove] = useState<OccurrenceView | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const records = useMemo(() => [...(completions.data ?? [])].reverse(), [completions.data]);
  const userNames = useMemo(() => new Map((users.data ?? []).map((user) => [user._id, user.name])), [users.data]);
  const dayFormatter = new Intl.DateTimeFormat(getLocale(), { dateStyle: 'long', timeZone: 'UTC' });
  const dateTimeFormatter = new Intl.DateTimeFormat(getLocale(), {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: timezone,
  });
  const formatDay = (day: string) => dayFormatter.format(new Date(`${day}T12:00:00.000Z`));

  const openEdit = (occurrence: OccurrenceView) => {
    if (!occurrence.completedAt || !occurrence.completedBy) return;
    setMessage(null);
    setEdit({
      occurrence,
      date: occurrence.date,
      completedAt: localDateTimeInput(occurrence.completedAt),
      completedBy: occurrence.completedBy,
    });
  };

  return (
    <section>
      <PageHeader title={t('completions.title')} />
      <p className="mb-6 max-w-3xl text-sm text-muted-foreground">{t('completions.explainer')}</p>

      <div className="mb-6 grid items-end gap-4 rounded-2xl border bg-card p-5 shadow-sm sm:grid-cols-[auto_1fr_1fr_auto]" role="search">
        <div className="hidden size-10 place-items-center rounded-xl bg-accent text-accent-foreground sm:grid">
          <Filter className="size-5" aria-hidden="true" />
        </div>
        <div className="flex flex-col gap-2">
          <Label htmlFor={`${idPrefix}-from`}>{t('completions.from')}</Label>
          <Input id={`${idPrefix}-from`} type="date" value={from} max={to} onChange={(event) => setFrom(event.target.value)} />
        </div>
        <div className="flex flex-col gap-2">
          <Label htmlFor={`${idPrefix}-to`}>{t('completions.to')}</Label>
          <Input id={`${idPrefix}-to`} type="date" value={to} min={from} onChange={(event) => setTo(event.target.value)} />
        </div>
        <Button type="button" variant="ghost" onClick={() => { resetFrom(); resetTo(); }}>
          {t('completions.resetFilters')}
        </Button>
      </div>

      {message && <p role="status" className="mb-4 rounded-xl bg-success/10 p-4 font-semibold text-success">{message}</p>}
      {completions.isPending || settings.isPending || users.isPending ? (
        <p role="status" className="text-muted-foreground">{t('app.loading')}</p>
      ) : completions.isError || settings.isError || users.isError ? (
        <p role="alert" className="rounded-xl bg-destructive/10 p-4 text-destructive">{t('app.error')}</p>
      ) : records.length === 0 ? (
        <EmptyState icon={<CheckCircle2 className="size-6" aria-hidden="true" />}>{t('completions.empty')}</EmptyState>
      ) : (
        <ul className="grid gap-3">
          {records.map((occurrence) => (
            <li key={occurrence._id} className="grid gap-4 rounded-xl border bg-card p-4 shadow-sm lg:grid-cols-[minmax(14rem,1.4fr)_repeat(3,minmax(9rem,1fr))_auto] lg:items-center">
              <div className="min-w-0">
                <p className="truncate font-bold">{occurrence.taskNameSnapshot}</p>
                <p className="truncate text-sm text-muted-foreground">{occurrence.roomNameSnapshot ?? t('tasks.unknownRoom')}</p>
              </div>
              <div>
                <p className="text-xs font-semibold text-muted-foreground">{t('completions.taskDate')}</p>
                <p>{formatDay(occurrence.date)}</p>
              </div>
              <div>
                <p className="text-xs font-semibold text-muted-foreground">{t('completions.completedAt')}</p>
                <p>{occurrence.completedAt ? dateTimeFormatter.format(new Date(occurrence.completedAt)) : t('history.value.none')}</p>
              </div>
              <div>
                <p className="text-xs font-semibold text-muted-foreground">{t('completions.completedBy')}</p>
                <p>{occurrence.completedBy ? (userNames.get(occurrence.completedBy) ?? t('tasks.unknownUser')) : t('history.value.none')}</p>
              </div>
              <div className="flex gap-2 lg:justify-end">
                <Button type="button" variant="outline" size="icon" title={t('common.edit')} aria-label={format('completions.editNamed', { task: occurrence.taskNameSnapshot })} onClick={() => openEdit(occurrence)}>
                  <Pencil aria-hidden="true" />
                </Button>
                <Button type="button" variant="outline" size="icon" className="text-destructive hover:text-destructive" title={t('completions.delete')} aria-label={format('completions.deleteNamed', { task: occurrence.taskNameSnapshot })} onClick={() => { setMessage(null); setRemove(occurrence); }}>
                  <Trash2 aria-hidden="true" />
                </Button>
              </div>
            </li>
          ))}
        </ul>
      )}

      <Dialog open={edit !== null} onOpenChange={(open) => { if (!open) setEdit(null); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{edit ? format('completions.editTitle', { task: edit.occurrence.taskNameSnapshot }) : t('completions.title')}</DialogTitle>
            <DialogDescription>{t('completions.editDescription')}</DialogDescription>
          </DialogHeader>
          {edit && (
            <div className="grid gap-4">
              <div className="grid gap-2">
                <Label htmlFor={`${idPrefix}-date`}>{t('completions.taskDate')}</Label>
                <Input id={`${idPrefix}-date`} type="date" required value={edit.date} onChange={(event) => setEdit({ ...edit, date: event.target.value })} />
              </div>
              <div className="grid gap-2">
                <Label htmlFor={`${idPrefix}-completed-at`}>{t('completions.completedAt')}</Label>
                <Input id={`${idPrefix}-completed-at`} type="datetime-local" required value={edit.completedAt} onChange={(event) => setEdit({ ...edit, completedAt: event.target.value })} />
              </div>
              <div className="grid gap-2">
                <Label htmlFor={`${idPrefix}-completed-by`}>{t('completions.completedBy')}</Label>
                <NativeSelect id={`${idPrefix}-completed-by`} value={edit.completedBy} onChange={(event) => setEdit({ ...edit, completedBy: event.target.value })}>
                  {(users.data ?? []).map((user) => <option key={user._id} value={user._id}>{user.name}</option>)}
                </NativeSelect>
              </div>
            </div>
          )}
          <DialogFooter>
            <Button type="button" variant="ghost" disabled={editCompletion.isPending} onClick={() => setEdit(null)}>{t('common.cancel')}</Button>
            <Button
              type="button"
              disabled={!edit?.date || !edit.completedAt || !edit.completedBy || editCompletion.isPending}
              onClick={() => edit && editCompletion.mutate({ id: edit.occurrence._id, date: edit.date, completedAt: new Date(edit.completedAt).toISOString(), completedBy: edit.completedBy }, { onSuccess: () => { setEdit(null); setMessage(t('completions.saved')); } })}
            >
              {t('common.save')}
            </Button>
          </DialogFooter>
          {editCompletion.isError && <p role="alert" className="text-sm font-semibold text-destructive">{t('completions.saveError')}</p>}
        </DialogContent>
      </Dialog>

      <Dialog open={remove !== null} onOpenChange={(open) => { if (!open) setRemove(null); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{remove ? format('completions.deleteTitle', { task: remove.taskNameSnapshot }) : t('completions.delete')}</DialogTitle>
            <DialogDescription>{t('completions.deleteDescription')}</DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button type="button" variant="ghost" disabled={deleteCompletion.isPending} onClick={() => setRemove(null)}>{t('common.cancel')}</Button>
            <Button type="button" variant="destructive" disabled={deleteCompletion.isPending} onClick={() => remove && deleteCompletion.mutate(remove._id, { onSuccess: () => { setRemove(null); setMessage(t('completions.deleted')); } })}>
              <Trash2 aria-hidden="true" />
              {t('completions.deleteConfirm')}
            </Button>
          </DialogFooter>
          {deleteCompletion.isError && <p role="alert" className="text-sm font-semibold text-destructive">{t('completions.deleteError')}</p>}
        </DialogContent>
      </Dialog>
    </section>
  );
}

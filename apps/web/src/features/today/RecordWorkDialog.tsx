import type { OccurrenceView } from '@huishoudplanner/shared';
import { CirclePlus, Sparkles, TriangleAlert } from 'lucide-react';
import { useId, useMemo, useRef, useState, type FormEvent } from 'react';
import { NativeSelect } from '@/components/NativeSelect';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { cn } from '@/lib/utils';
import { useRooms, useTasks } from '../../api/queries.ts';
import { createRequestKey } from '../../api/requestKey.ts';
import { format, t } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { useRecordWork } from './api.ts';
import { shortDate } from './OccurrenceItem.tsx';
import { buildRecordWork, type RecordWorkField, type RecordWorkForm, type RecordWorkKind } from './recordWorkModel.ts';

interface RecordWorkDialogProps {
  open: boolean;
  onOpenChange(open: boolean): void;
  /** The day the work is recorded on; recorded work is always done today. */
  todayKey: string;
  /** Opens on "extra" with this task chosen (the Overdue page records an extra for one task). */
  initialTaskId?: string;
  onRecorded?(occurrence: OccurrenceView): void;
}

/** Records work that was done and that the plan did not ask for: an extra execution or a one-off task. */
export function RecordWorkDialog({ open, onOpenChange, todayKey, initialTaskId, onRecorded }: RecordWorkDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[calc(100dvh-2rem)] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>{t('recordWork.title')}</DialogTitle>
          <DialogDescription>{t('recordWork.description')}</DialogDescription>
        </DialogHeader>
        {/* Mounted only while open, so every opening starts with an empty form and a new request key. */}
        <RecordWorkFormBody
          todayKey={todayKey}
          initialTaskId={initialTaskId}
          onCancel={() => onOpenChange(false)}
          onRecorded={(occurrence) => {
            onRecorded?.(occurrence);
            onOpenChange(false);
          }}
        />
      </DialogContent>
    </Dialog>
  );
}

const KINDS: { kind: RecordWorkKind; label: 'recordWork.kind.extra' | 'recordWork.kind.oneOff'; hint: 'recordWork.kind.extraHint' | 'recordWork.kind.oneOffHint' }[] = [
  { kind: 'extra', label: 'recordWork.kind.extra', hint: 'recordWork.kind.extraHint' },
  { kind: 'oneOff', label: 'recordWork.kind.oneOff', hint: 'recordWork.kind.oneOffHint' },
];

function RecordWorkFormBody({
  todayKey,
  initialTaskId,
  onCancel,
  onRecorded,
}: {
  todayKey: string;
  initialTaskId?: string;
  onCancel(): void;
  onRecorded(occurrence: OccurrenceView): void;
}) {
  const idPrefix = useId();
  const { profile, activeUsers } = useProfile();
  const tasks = useTasks();
  const rooms = useRooms();
  const record = useRecordWork();
  const [chosen, setForm] = useState<RecordWorkForm>({
    kind: 'extra',
    taskId: initialTaskId ?? '',
    name: '',
    roomId: '',
    duration: '',
    doneBy: '',
  });
  // Until someone else is chosen, the person doing the recording did the work (the profile may still be loading).
  const form: RecordWorkForm = { ...chosen, doneBy: chosen.doneBy || (profile?._id ?? '') };
  const [submitted, setSubmitted] = useState(false);
  const [failed, setFailed] = useState(false);
  // One idempotency key per intent: a repeated click or retry of the same values reuses it, changed values
  // are a new intent, and a successful request drops it.
  const intent = useRef<{ signature: string; key: string } | null>(null);
  // A second click can arrive before the pending state has rendered, so the guard is synchronous.
  const inFlight = useRef(false);

  const activeTasks = useMemo(
    () => (tasks.data ?? []).filter((task) => task.active).sort((a, b) => a.name.localeCompare(b.name)),
    [tasks.data],
  );
  const activeRooms = useMemo(() => (rooms.data ?? []).filter((room) => room.active), [rooms.data]);
  const roomNames = useMemo(() => new Map((rooms.data ?? []).map((room) => [room._id, room.name])), [rooms.data]);

  const result = buildRecordWork(form, todayKey);
  const errors = submitted && !result.ok ? result.errors : {};
  const set = <K extends keyof RecordWorkForm>(key: K, value: RecordWorkForm[K]) => {
    setFailed(false);
    setForm((current) => ({ ...current, [key]: value }));
  };
  const fieldProps = (field: RecordWorkField) => ({
    'aria-invalid': errors[field] ? true : undefined,
    'aria-describedby': errors[field] ? `${idPrefix}-${field}-error` : undefined,
  });
  const fieldError = (field: RecordWorkField) =>
    errors[field] ? (
      <p id={`${idPrefix}-${field}-error`} role="alert" className="flex items-center gap-1.5 text-sm font-semibold text-destructive">
        <TriangleAlert className="size-4 shrink-0" aria-hidden="true" />
        {t(errors[field])}
      </p>
    ) : null;

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (inFlight.current) return;
    setSubmitted(true);
    if (!result.ok) return;
    const signature = JSON.stringify(result.body);
    if (intent.current?.signature !== signature) intent.current = { signature, key: createRequestKey() };
    inFlight.current = true;
    setFailed(false);
    try {
      const occurrence = await record.mutateAsync({ ...result.body, requestId: intent.current.key });
      intent.current = null;
      onRecorded(occurrence);
    } catch {
      setFailed(true);
    } finally {
      inFlight.current = false;
    }
  };

  const pending = record.isPending;
  const noTasks = tasks.isSuccess && activeTasks.length === 0;

  return (
    <form className="grid gap-5" onSubmit={submit} noValidate aria-label={t('recordWork.title')}>
      <fieldset className="grid gap-2">
        <legend className="mb-1 text-sm font-semibold">{t('recordWork.kind')}</legend>
        {KINDS.map(({ kind, label, hint }) => {
          const selected = form.kind === kind;
          const Icon = kind === 'extra' ? CirclePlus : Sparkles;
          return (
            <label
              key={kind}
              className={cn(
                'flex cursor-pointer items-start gap-3 rounded-xl border-2 p-3 transition-colors has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring/50',
                selected ? 'border-primary bg-primary/5' : 'border-border bg-card hover:bg-secondary/50',
              )}
            >
              <input
                type="radio"
                name={`${idPrefix}-kind`}
                className="mt-1 size-5 shrink-0 accent-primary"
                checked={selected}
                onChange={() => set('kind', kind)}
                aria-labelledby={`${idPrefix}-${kind}-label`}
                aria-describedby={`${idPrefix}-${kind}-hint`}
              />
              <Icon className="mt-0.5 size-5 shrink-0 text-primary" aria-hidden="true" />
              <span className="grid min-w-0 gap-0.5">
                <span id={`${idPrefix}-${kind}-label`} className="leading-snug font-bold">
                  {t(label)}
                </span>
                <span id={`${idPrefix}-${kind}-hint`} className="text-sm text-muted-foreground">
                  {t(hint)}
                </span>
              </span>
            </label>
          );
        })}
      </fieldset>

      {form.kind === 'extra' ? (
        <div className="grid gap-1.5">
          <Label htmlFor={`${idPrefix}-task`}>{t('recordWork.task')}</Label>
          <NativeSelect
            id={`${idPrefix}-task`}
            className="[&_select]:h-11"
            value={form.taskId}
            onChange={(event) => set('taskId', event.target.value)}
            {...fieldProps('taskId')}
          >
            <option value="">{t('recordWork.taskPlaceholder')}</option>
            {activeTasks.map((task) => (
              <option key={task._id} value={task._id}>
                {[task.name, roomNames.get(task.roomId)].filter(Boolean).join(' · ')}
              </option>
            ))}
          </NativeSelect>
          {noTasks && <p className="text-sm text-muted-foreground">{t('recordWork.noTasks')}</p>}
          {fieldError('taskId')}
        </div>
      ) : (
        <>
          <div className="grid gap-1.5">
            <Label htmlFor={`${idPrefix}-name`}>{t('recordWork.name')}</Label>
            <Input
              id={`${idPrefix}-name`}
              className="h-11"
              value={form.name}
              maxLength={120}
              autoComplete="off"
              onChange={(event) => set('name', event.target.value)}
              {...fieldProps('name')}
            />
            {fieldError('name')}
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            <div className="grid gap-1.5">
              <Label htmlFor={`${idPrefix}-room`}>{t('recordWork.room')}</Label>
              <NativeSelect
                id={`${idPrefix}-room`}
                className="[&_select]:h-11"
                value={form.roomId}
                onChange={(event) => set('roomId', event.target.value)}
              >
                <option value="">{t('tasks.noRoom')}</option>
                {activeRooms.map((room) => (
                  <option key={room._id} value={room._id}>
                    {room.name}
                  </option>
                ))}
              </NativeSelect>
            </div>
            <div className="grid gap-1.5">
              <Label htmlFor={`${idPrefix}-duration`}>{t('recordWork.duration')}</Label>
              <Input
                id={`${idPrefix}-duration`}
                className="h-11"
                type="number"
                inputMode="numeric"
                min={1}
                step={1}
                value={form.duration}
                onChange={(event) => set('duration', event.target.value)}
                {...fieldProps('duration')}
              />
              {fieldError('duration')}
            </div>
          </div>
        </>
      )}

      <div className="grid gap-1.5">
        <Label htmlFor={`${idPrefix}-done-by`}>{t('recordWork.doneBy')}</Label>
        <NativeSelect
          id={`${idPrefix}-done-by`}
          className="[&_select]:h-11"
          value={form.doneBy}
          onChange={(event) => set('doneBy', event.target.value)}
          {...fieldProps('doneBy')}
        >
          {form.doneBy === '' && <option value="" />}
          {activeUsers.map((user) => (
            <option key={user._id} value={user._id}>
              {user.name}
            </option>
          ))}
        </NativeSelect>
        {fieldError('doneBy')}
      </div>

      <p className="text-sm text-muted-foreground">{format('recordWork.doneNow', { date: shortDate(todayKey) })}</p>

      {failed && (
        <p role="alert" className="flex items-center gap-2 rounded-xl bg-destructive/10 p-3 text-sm font-semibold text-destructive">
          <TriangleAlert className="size-4 shrink-0" aria-hidden="true" />
          {t('recordWork.error.failed')}
        </p>
      )}

      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <Button type="button" variant="outline" className="h-11 rounded-full" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button type="submit" className="h-11 rounded-full" disabled={pending}>
          {pending ? t('recordWork.submitting') : t('recordWork.submit')}
        </Button>
      </div>
    </form>
  );
}

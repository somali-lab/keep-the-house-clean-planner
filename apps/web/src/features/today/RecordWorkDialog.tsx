import type { OccurrenceView } from '@huishoudplanner/shared';
import { CirclePlus, ClipboardCheck, Sparkles, TriangleAlert } from 'lucide-react';
import { useId, useMemo, useRef, useState, type FormEvent } from 'react';
import { NativeSelect } from '@/components/NativeSelect';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { cn } from '@/lib/utils';
import { useRooms, useTasks } from '../../api/queries.ts';
import { format, t } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { useCheckOffPlanned, useOccurrences, useRecordWork } from './api.ts';
import { shortDate } from './OccurrenceItem.tsx';
import { buildRecordWork, type RecordWorkField, type RecordWorkForm, type RecordWorkKind } from './recordWorkModel.ts';

interface RecordWorkDialogProps {
  open: boolean;
  onOpenChange(open: boolean): void;
  /** The day the work is recorded on; recorded work is always done today. */
  todayKey: string;
  /** Opens on "extra" with this task chosen (the Overdue page records an extra for one task). */
  initialTaskId?: string;
  /** `checkedOff`: the planned occurrence of the task was completed instead of recording an extra one. */
  onRecorded?(occurrence: OccurrenceView, how: RecordWorkHow): void;
}

export type RecordWorkHow = 'recorded' | 'checkedOff';

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
          onRecorded={(occurrence, how) => {
            onRecorded?.(occurrence, how);
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

const FIELD_ORDER: RecordWorkField[] = ['taskId', 'name', 'duration', 'doneBy'];
/** Id suffix of the control of each field. */
const FIELD_CONTROL: Record<RecordWorkField, string> = { taskId: 'task', name: 'name', duration: 'duration', doneBy: 'done-by' };

const PLANNED_CHOICES: {
  value: 'checkOff' | 'extra';
  label: 'recordWork.planned.checkOff' | 'recordWork.planned.extra';
  hint: 'recordWork.planned.checkOffHint' | 'recordWork.planned.extraHint';
}[] = [
  { value: 'checkOff', label: 'recordWork.planned.checkOff', hint: 'recordWork.planned.checkOffHint' },
  { value: 'extra', label: 'recordWork.planned.extra', hint: 'recordWork.planned.extraHint' },
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
  onRecorded(occurrence: OccurrenceView, how: RecordWorkHow): void;
}) {
  const idPrefix = useId();
  const { profile, activeUsers } = useProfile();
  const tasks = useTasks();
  const rooms = useRooms();
  const record = useRecordWork();
  const checkOff = useCheckOffPlanned();
  const todayOccurrences = useOccurrences(todayKey, todayKey);
  const formRef = useRef<HTMLFormElement>(null);
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
  // When the chosen task is still planned today, checking that off is the default: it keeps one execution in the history.
  const [plannedChoice, setPlannedChoice] = useState<'checkOff' | 'extra'>('checkOff');
  const [submitted, setSubmitted] = useState(false);
  const [failed, setFailed] = useState(false);
  // A second click can arrive before the pending state has rendered, so the guard is synchronous.
  const inFlight = useRef(false);

  const activeTasks = useMemo(
    () => (tasks.data ?? []).filter((task) => task.active).sort((a, b) => a.name.localeCompare(b.name)),
    [tasks.data],
  );
  const activeRooms = useMemo(() => (rooms.data ?? []).filter((room) => room.active), [rooms.data]);
  const roomNames = useMemo(() => new Map((rooms.data ?? []).map((room) => [room._id, room.name])), [rooms.data]);

  const planned =
    form.kind === 'extra' && form.taskId
      ? (todayOccurrences.data ?? []).find(
          (occurrence) => occurrence.taskId === form.taskId && occurrence.status === 'open' && occurrence.date === todayKey,
        )
      : undefined;
  const checkingOff = planned !== undefined && plannedChoice === 'checkOff';
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
  // The summary below is the one alert; each field's message is linked through aria-describedby.
  const fieldError = (field: RecordWorkField) =>
    errors[field] ? (
      <p id={`${idPrefix}-${field}-error`} className="flex items-center gap-1.5 text-sm font-semibold text-destructive">
        <TriangleAlert className="size-4 shrink-0" aria-hidden="true" />
        {t(errors[field])}
      </p>
    ) : null;

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (inFlight.current) return;
    setSubmitted(true);
    if (!result.ok) {
      // Move focus to the first field that needs attention.
      const first = FIELD_ORDER.find((field) => result.errors[field]);
      if (first) formRef.current?.querySelector<HTMLElement>(`[id="${idPrefix}-${FIELD_CONTROL[first]}"]`)?.focus();
      return;
    }
    inFlight.current = true;
    setFailed(false);
    try {
      if (planned && plannedChoice === 'checkOff') {
        onRecorded(await checkOff.mutateAsync({ id: planned._id, completedBy: form.doneBy }), 'checkedOff');
      } else {
        onRecorded(await record.mutateAsync(result.body), 'recorded');
      }
    } catch {
      setFailed(true);
    } finally {
      inFlight.current = false;
    }
  };

  const pending = record.isPending || checkOff.isPending;
  const errorFields = FIELD_ORDER.filter((field) => errors[field]);
  const noTasks = tasks.isSuccess && activeTasks.length === 0;

  return (
    <form ref={formRef} className="grid gap-5" onSubmit={submit} noValidate aria-label={t('recordWork.title')}>
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
            onChange={(event) => {
              set('taskId', event.target.value);
              setPlannedChoice('checkOff');
            }}
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
          {planned && (
            <fieldset className="mt-2 grid gap-2 rounded-xl border-2 border-warning bg-warning/10 p-3">
              <legend className="flex items-center gap-1.5 px-1 text-sm font-semibold">
                <TriangleAlert className="size-4 shrink-0" aria-hidden="true" />
                {format('recordWork.planned.notice', { task: planned.taskNameSnapshot })}
              </legend>
              {PLANNED_CHOICES.map(({ value, label, hint }) => (
                <label
                  key={value}
                  className="flex cursor-pointer items-start gap-3 rounded-lg p-2 has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring/50"
                >
                  <input
                    type="radio"
                    name={`${idPrefix}-planned`}
                    className="mt-1 size-5 shrink-0 accent-primary"
                    checked={plannedChoice === value}
                    onChange={() => setPlannedChoice(value)}
                    aria-labelledby={`${idPrefix}-planned-${value}-label`}
                    aria-describedby={`${idPrefix}-planned-${value}-hint`}
                  />
                  <span className="grid gap-0.5">
                    <span id={`${idPrefix}-planned-${value}-label`} className="leading-snug font-bold">
                      {t(label)}
                    </span>
                    <span id={`${idPrefix}-planned-${value}-hint`} className="text-sm text-muted-foreground">
                      {t(hint)}
                    </span>
                  </span>
                </label>
              ))}
            </fieldset>
          )}
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

      {errorFields.length > 0 && (
        <p role="alert" className="flex items-center gap-2 rounded-xl bg-destructive/10 p-3 text-sm font-semibold text-destructive">
          <TriangleAlert className="size-4 shrink-0" aria-hidden="true" />
          {t('recordWork.error.summary')}
        </p>
      )}

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
        <Button type="submit" className="h-11 rounded-full" disabled={pending || !profile}>
          {checkingOff && !pending && <ClipboardCheck aria-hidden="true" />}
          {pending ? t('recordWork.submitting') : checkingOff ? t('recordWork.checkOff') : t('recordWork.submit')}
        </Button>
      </div>
    </form>
  );
}

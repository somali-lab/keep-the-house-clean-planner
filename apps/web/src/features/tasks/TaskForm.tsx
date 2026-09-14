import type { Interval, Room, User } from '@huishoudplanner/shared';
import { useId, useState, type FormEvent, type ReactNode } from 'react';
import { NativeSelect } from '@/components/NativeSelect';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { cn } from '@/lib/utils';
import { t } from '../../i18n/nl.ts';
import { validateTaskForm, type TaskFormErrors, type TaskFormValues } from './taskForm.ts';

export interface TaskFormProps {
  title: string;
  className?: string;
  showTitle?: boolean;
  initial: TaskFormValues;
  rooms: Room[];
  intervals: Interval[];
  users: User[];
  submitting?: boolean;
  /** Field errors reported by the server after submit. */
  serverErrors?: TaskFormErrors;
  onSubmit(values: TaskFormValues): void;
  onCancel(): void;
}

function Field({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cn('flex flex-col gap-2', className)}>{children}</div>;
}

export function TaskForm({
  title,
  className,
  showTitle = true,
  initial,
  rooms,
  intervals,
  users,
  submitting = false,
  serverErrors = {},
  onSubmit,
  onCancel,
}: TaskFormProps) {
  const idPrefix = useId();
  const [values, setValues] = useState(initial);
  const [errors, setErrors] = useState<TaskFormErrors>({});
  const shown: TaskFormErrors = { ...serverErrors, ...errors };

  const set = (field: keyof TaskFormValues) => (value: string) => {
    setValues((v) => ({ ...v, [field]: value }));
    setErrors((e) => ({ ...e, [field]: undefined }));
  };

  const handleSubmit = (event: FormEvent) => {
    event.preventDefault();
    const found = validateTaskForm(values);
    setErrors(found);
    if (Object.keys(found).length === 0) onSubmit(values);
  };

  const fieldProps = (field: keyof TaskFormValues) => ({
    id: `${idPrefix}-${field}`,
    'aria-invalid': shown[field] ? true : undefined,
    'aria-describedby': shown[field] ? `${idPrefix}-${field}-error` : undefined,
  });

  const errorFor = (field: keyof TaskFormValues) =>
    shown[field] ? (
      <p
        id={`${idPrefix}-${field}-error`}
        className="text-sm font-semibold text-destructive"
        role="alert"
      >
        {t(shown[field])}
      </p>
    ) : null;

  return (
    <form
      className={cn('rounded-2xl border bg-card p-6 shadow-sm', className)}
      noValidate
      onSubmit={handleSubmit}
      aria-label={title}
    >
      {showTitle && <h2 className="mb-5">{title}</h2>}

      <div className="grid grid-cols-1 gap-x-5 gap-y-4 sm:grid-cols-2">
        <Field className="sm:col-span-2">
          <Label htmlFor={`${idPrefix}-name`}>{t('tasks.field.name')}</Label>
          <Input
            {...fieldProps('name')}
            value={values.name}
            onChange={(e) => set('name')(e.target.value)}
          />
          {errorFor('name')}
        </Field>

        <Field>
          <Label htmlFor={`${idPrefix}-roomId`}>{t('tasks.field.room')}</Label>
          <NativeSelect
            {...fieldProps('roomId')}
            value={values.roomId}
            onChange={(e) => set('roomId')(e.target.value)}
          >
            <option value="">{t('tasks.field.choose')}</option>
            {rooms.map((room) => (
              <option key={room._id} value={room._id}>
                {room.name}
              </option>
            ))}
          </NativeSelect>
          {errorFor('roomId')}
        </Field>

        <Field>
          <Label htmlFor={`${idPrefix}-intervalKey`}>{t('tasks.field.interval')}</Label>
          <NativeSelect
            {...fieldProps('intervalKey')}
            value={values.intervalKey}
            onChange={(e) => set('intervalKey')(e.target.value)}
          >
            <option value="">{t('tasks.field.choose')}</option>
            {intervals.map((interval) => (
              <option key={interval.key} value={interval.key}>
                {interval.label}
              </option>
            ))}
          </NativeSelect>
          {errorFor('intervalKey')}
        </Field>

        <Field>
          <Label htmlFor={`${idPrefix}-durationMinutes`}>{t('tasks.field.duration')}</Label>
          <Input
            {...fieldProps('durationMinutes')}
            type="number"
            inputMode="numeric"
            min={1}
            step={1}
            required
            className="h-10 bg-card"
            value={values.durationMinutes}
            onChange={(e) => set('durationMinutes')(e.target.value)}
          />
          {errorFor('durationMinutes')}
        </Field>

        <Field>
          <Label htmlFor={`${idPrefix}-defaultAssigneeId`}>{t('tasks.field.assignee')}</Label>
          <NativeSelect
            {...fieldProps('defaultAssigneeId')}
            value={values.defaultAssigneeId}
            onChange={(e) => set('defaultAssigneeId')(e.target.value)}
          >
            <option value="">{t('tasks.anyone')}</option>
            {users.map((user) => (
              <option key={user._id} value={user._id}>
                {user.name}
              </option>
            ))}
          </NativeSelect>
          {errorFor('defaultAssigneeId')}
        </Field>

        <Field className="sm:col-span-2">
          <Label htmlFor={`${idPrefix}-notes`}>{t('tasks.field.notes')}</Label>
          <Textarea
            {...fieldProps('notes')}
            className="bg-card"
            value={values.notes}
            onChange={(e) => set('notes')(e.target.value)}
          />
        </Field>

        <Field className="sm:col-span-2">
          <Label htmlFor={`${idPrefix}-tags`}>{t('tasks.field.tags')}</Label>
          <Input
            {...fieldProps('tags')}
            value={values.tags}
            onChange={(e) => set('tags')(e.target.value)}
          />
        </Field>
      </div>

      <div className="mt-6 flex flex-wrap gap-2">
        <Button type="submit" disabled={submitting}>
          {t('common.save')}
        </Button>
        <Button type="button" variant="ghost" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
      </div>
    </form>
  );
}

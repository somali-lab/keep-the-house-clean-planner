import { ImagePlus, Medal, Pencil, Plus, Save, Sparkles, Trash2, X } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { useId, useMemo, useRef, useState, type FormEvent } from 'react';
import { EmptyState } from '@/components/EmptyState';
import { NativeSelect } from '@/components/NativeSelect';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { cn } from '@/lib/utils';
import { ApiRequestError, isStaleEntity } from '../../api/index.ts';
import { useLimits, useTasks, type Task } from '../../api/v2/queries.ts';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import { getLanguage } from '../../i18n/runtime.ts';
import { checkboxClass, Field, FormActions, FormMessage, listRowClass } from '../settings/SettingsCard.tsx';
import { BADGE_RULE_TYPES, badgeKeys, useAddExampleBadges, useBadges, useDeleteBadge, useSaveBadge, type Badge } from './api.ts';
import { BadgeImage } from './BadgeImage.tsx';
import {
  buildBadgeSave,
  EMPTY_BADGE_FORM,
  formFromBadge,
  limitedNames,
  readImageFile,
  ruleText,
  type BadgeForm,
  type BadgeFormErrors,
  type BadgeLimits,
} from './badgeModel.ts';

const RULE_TYPE_LABEL = {
  executions: 'badges.ruleType.executions',
  minutes: 'badges.ruleType.minutes',
  onTimeWeeks: 'badges.ruleType.onTimeWeeks',
} as const satisfies Record<(typeof BADGE_RULE_TYPES)[number], MessageKey>;

const THRESHOLD_LABEL = {
  executions: 'badges.threshold.executions',
  minutes: 'badges.threshold.minutes',
  onTimeWeeks: 'badges.threshold.onTimeWeeks',
} as const satisfies Record<(typeof BADGE_RULE_TYPES)[number], MessageKey>;

/** The reasons of a refused request: the problem lists them as `errors: { field: [reason] }`. */
function refusedReasons(error: ApiRequestError): string[] {
  const details = error.details;
  if (typeof details !== 'object' || details === null || Array.isArray(details)) return [];
  return Object.values(details).flatMap((reasons) => (Array.isArray(reasons) ? reasons.filter((reason): reason is string => typeof reason === 'string') : []));
}

/** A server answer about the image, the limit or a changed badge, in words of the person. */
function saveError(error: unknown): string {
  if (isStaleEntity(error)) return t('app.staleEntity');
  if (error instanceof ApiRequestError) {
    if (error.code === 'validation_error') {
      const reasons = refusedReasons(error);
      if (reasons.includes('image_too_large')) return t('badges.error.imageSize');
      if (reasons.some((reason) => reason === 'unsupported_image_type' || reason === 'image_type_mismatch' || reason === 'invalid_base64')) {
        return t('badges.error.imageType');
      }
    }
    if (error.code === 'badge_limit') {
      const limit = typeof error.details === 'object' && error.details !== null && 'limit' in error.details ? Number(error.details.limit) : NaN;
      return Number.isFinite(limit) ? format('badges.error.limit', { limit }) : t('badges.error.limitUnknown');
    }
  }
  return t('badges.error.save');
}

/**
 * The page where an administrator manages badges (ADR-0014): create, change, deactivate and delete them, upload the
 * picture of each, and add the example badges.
 */
export function BadgesPage() {
  const queryClient = useQueryClient();
  const badges = useBadges();
  const tasks = useTasks();
  const limits = useLimits();
  const [editing, setEditing] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<Badge | null>(null);
  const [message, setMessage] = useState<{ kind: 'status' | 'alert'; text: string } | null>(null);
  const addExamples = useAddExampleBadges();
  const remove = useDeleteBadge();

  const taskNames = useMemo(() => new Map((tasks.data ?? []).map((task) => [task.id, task.name])), [tasks.data]);

  const closeDelete = () => {
    remove.reset();
    setDeleting(null);
  };

  const open = (id: string | null) => {
    setMessage(null);
    setEditing(id);
  };

  const addExampleBadges = () => {
    setMessage(null);
    addExamples.mutate(getLanguage(), {
      onSuccess: (result) =>
        setMessage(
          result.created.length === 0
            ? { kind: 'status', text: t('badges.examplesExist') }
            : {
                kind: 'status',
                text: [
                  format('badges.examplesAdded', { count: result.created.length }),
                  result.created.some((badge) => !badge.active) ? t('badges.examplesInactive') : '',
                ]
                  .filter(Boolean)
                  .join(' '),
              },
        ),
      onError: () => setMessage({ kind: 'alert', text: t('badges.examplesError') }),
    });
  };

  return (
    <section className="flex flex-col gap-6">
      <PageHeader
        title={t('badges.title')}
        description={t('badges.description')}
        actions={
          <>
            <Button type="button" variant="outline" disabled={addExamples.isPending} onClick={addExampleBadges}>
              <Sparkles aria-hidden="true" />
              {t('badges.addExamples')}
            </Button>
            <Button type="button" onClick={() => open('new')} disabled={editing === 'new'}>
              <Plus aria-hidden="true" />
              {t('badges.add')}
            </Button>
          </>
        }
      />
      {message && <FormMessage kind={message.kind}>{message.text}</FormMessage>}
      {editing === 'new' && limits.data && (
        <BadgeEditor
          tasks={tasks.data ?? []}
          limits={limits.data.badges}
          onSaved={() => {
            setEditing(null);
            setMessage({ kind: 'status', text: t('badges.saved') });
          }}
          onCancel={() => open(null)}
        />
      )}
      {badges.isPending || limits.isPending ? (
        <p role="status" className="text-muted-foreground">
          {t('app.loading')}
        </p>
      ) : badges.isError || limits.isError ? (
        <FormMessage kind="alert">{t('badges.loadError')}</FormMessage>
      ) : badges.data.length === 0 && editing !== 'new' ? (
        <EmptyState icon={<Medal className="size-6" aria-hidden="true" />}>{t('badges.empty')}</EmptyState>
      ) : (
        <ul className="flex flex-col gap-3" aria-label={t('badges.list')}>
          {badges.data.map((badge) => (
            <li key={badge.id}>
              {editing === badge.id ? (
                <BadgeEditor
                  badge={badge}
                  tasks={tasks.data ?? []}
                  limits={limits.data.badges}
                  onSaved={() => {
                    setEditing(null);
                    setMessage({ kind: 'status', text: t('badges.saved') });
                  }}
                  onCancel={() => open(null)}
                />
              ) : (
                <BadgeRow badge={badge} taskNames={taskNames} onEdit={() => open(badge.id)} onDelete={() => { remove.reset(); setDeleting(badge); }} />
              )}
            </li>
          ))}
        </ul>
      )}
      <Dialog open={deleting !== null} onOpenChange={(next) => !next && !remove.isPending && closeDelete()}>
        {deleting && (
          <DialogContent>
            <DialogHeader>
              <DialogTitle>{format('badges.deleteConfirmTitle', { name: deleting.name })}</DialogTitle>
              <DialogDescription>{t('badges.deleteConfirmBody')}</DialogDescription>
            </DialogHeader>
            {remove.isError && <FormMessage kind="alert">{isStaleEntity(remove.error) ? t('app.staleEntity') : t('badges.deleteError')}</FormMessage>}
            <DialogFooter>
              <Button type="button" variant="outline" onClick={closeDelete}>
                {t('common.cancel')}
              </Button>
              <Button
                type="button"
                variant="destructive"
                disabled={remove.isPending}
                onClick={() =>
                  remove.mutate(deleting, {
                    onSuccess: () => {
                      setDeleting(null);
                      setMessage(null);
                    },
                    // A stale delete wrote nothing and the list was read again: the dialog asks for the badge as it is now, and a
                    // badge that is gone needs no delete.
                    onError: (error) => {
                      if (!isStaleEntity(error)) return;
                      const fresh = queryClient.getQueryData<Badge[]>(badgeKeys.list)?.find((badge) => badge.id === deleting.id);
                      setDeleting((current) => (current?.id === deleting.id ? (fresh ?? null) : current));
                    },
                  })
                }
              >
                <Trash2 aria-hidden="true" />
                {t('badges.deleteConfirm')}
              </Button>
            </DialogFooter>
          </DialogContent>
        )}
      </Dialog>
    </section>
  );
}

function BadgeRow({
  badge,
  taskNames,
  onEdit,
  onDelete,
}: {
  badge: Badge;
  taskNames: ReadonlyMap<string, string>;
  onEdit(): void;
  onDelete(): void;
}) {
  const rule = ruleText(badge.rule, taskNames);
  const { shown, more } = limitedNames(rule.tasks);
  const tasks =
    rule.key === 'badges.rule.onTimeWeeks'
      ? ''
      : shown.length === 0
        ? t('badges.allTasks')
        : more > 0
          ? format('badges.moreTasks', { names: shown.join(', '), count: more })
          : shown.join(', ');
  return (
    <div className={cn(listRowClass, 'items-start')}>
      <BadgeImage badge={badge} muted={!badge.active} />
      <div className="min-w-0 flex-1">
        <p className="flex flex-wrap items-center gap-2">
          <strong className="font-bold [overflow-wrap:anywhere]">{badge.name}</strong>
          {!badge.active && <span className="text-sm text-muted-foreground">({t('badges.inactive')})</span>}
          {badge.exampleKey && <span className="rounded-full bg-secondary px-2 py-0.5 text-xs font-semibold">{t('badges.example')}</span>}
        </p>
        {badge.description && <p className="text-sm text-muted-foreground [overflow-wrap:anywhere]">{badge.description}</p>}
        <p className="mt-1 text-sm">{format(rule.key, { count: rule.count, tasks })}</p>
      </div>
      <div className="ml-auto flex items-center gap-1">
        <Button type="button" variant="ghost" size="sm" aria-label={format('badges.edit', { name: badge.name })} onClick={onEdit}>
          <Pencil aria-hidden="true" />
          {t('common.edit')}
        </Button>
        <Button
          type="button"
          variant="ghost"
          size="icon-sm"
          className="text-destructive hover:text-destructive"
          aria-label={format('badges.delete', { name: badge.name })}
          onClick={onDelete}
        >
          <Trash2 aria-hidden="true" />
        </Button>
      </div>
    </div>
  );
}

/** The editor of one badge: name, description, rule, tasks, the picture and whether it is active. */
export function BadgeEditor({
  badge,
  tasks,
  limits,
  onSaved,
  onCancel,
}: {
  badge?: Badge;
  tasks: Task[];
  limits: BadgeLimits;
  onSaved(): void;
  onCancel(): void;
}) {
  const idPrefix = useId();
  const fileInput = useRef<HTMLInputElement>(null);
  const [form, setForm] = useState<BadgeForm>(() => (badge ? formFromBadge(badge) : EMPTY_BADGE_FORM));
  const [errors, setErrors] = useState<BadgeFormErrors>({});
  const [failure, setFailure] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const save = useSaveBadge();

  const update = (patch: Partial<BadgeForm>) => setForm((current) => ({ ...current, ...patch }));

  const visibleTasks = useMemo(() => {
    const query = search.trim().toLowerCase();
    return tasks.filter((task) => task.active || form.taskIds.includes(task.id)).filter((task) => !query || task.name.toLowerCase().includes(query));
  }, [tasks, search, form.taskIds]);

  const toggleTask = (id: string) =>
    update({ taskIds: form.taskIds.includes(id) ? form.taskIds.filter((other) => other !== id) : [...form.taskIds, id] });

  const chooseImage = async (file: File | undefined) => {
    if (!file) return;
    const result = await readImageFile(file, limits);
    if (!result.ok) {
      setErrors((current) => ({ ...current, image: result.error }));
      if (fileInput.current) fileInput.current.value = '';
      return;
    }
    setErrors((current) => ({ ...current, image: undefined }));
    update({ image: { kind: 'new', contentType: result.contentType, data: result.data, previewUrl: `data:${result.contentType};base64,${result.data}`, fileName: file.name } });
  };

  const removeImage = () => {
    setErrors((current) => ({ ...current, image: undefined }));
    if (fileInput.current) fileInput.current.value = '';
    update({ image: { kind: 'none' } });
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const built = buildBadgeSave(form, limits);
    if (!built.ok) {
      setErrors({ ...errors, ...built.errors });
      return;
    }
    if (errors.image) return;
    setErrors({});
    setFailure(null);
    save.mutate({ ...(badge ? { badge } : {}), create: built.create, patch: built.patch }, { onSuccess: onSaved, onError: (error) => setFailure(saveError(error)) });
  };

  const current = badge && form.image.kind === 'keep' ? badge : null;
  const preview = form.image.kind === 'new' ? form.image.previewUrl : (current?.image?.url ?? null);
  const nameOfBadge = form.name.trim() || badge?.name || t('badges.new');
  const errorId = (field: string) => `${idPrefix}-${field}-error`;

  return (
    <form
      className="flex flex-col gap-5 rounded-xl border border-primary/25 bg-secondary/40 p-5"
      onSubmit={submit}
      noValidate
      aria-label={badge ? format('badges.edit', { name: badge.name }) : t('badges.new')}
    >
      <div className="grid gap-4 md:grid-cols-2">
        <Field>
          <Label htmlFor={`${idPrefix}-name`}>{t('badges.name')}</Label>
          <Input
            id={`${idPrefix}-name`}
            className="h-10 bg-card"
            value={form.name}
            maxLength={limits.maxNameLength}
            aria-invalid={errors.name ? true : undefined}
            aria-describedby={errors.name ? errorId('name') : undefined}
            onChange={(e) => update({ name: e.target.value })}
          />
          {errors.name && (
            <p id={errorId('name')} role="alert" className="text-sm font-semibold text-destructive">
              {t(errors.name)}
            </p>
          )}
        </Field>
        <Field>
          <Label htmlFor={`${idPrefix}-description`}>{t('badges.descriptionField')}</Label>
          <Input
            id={`${idPrefix}-description`}
            className="h-10 bg-card"
            value={form.description}
            maxLength={limits.maxDescriptionLength}
            aria-invalid={errors.description ? true : undefined}
            aria-describedby={errors.description ? errorId('description') : undefined}
            onChange={(e) => update({ description: e.target.value })}
          />
          {errors.description && (
            <p id={errorId('description')} role="alert" className="text-sm font-semibold text-destructive">
              {t(errors.description)}
            </p>
          )}
        </Field>
      </div>

      <div className="grid gap-4 md:grid-cols-[minmax(0,1fr)_12rem]">
        <Field>
          <Label htmlFor={`${idPrefix}-rule`}>{t('badges.ruleType')}</Label>
          <NativeSelect id={`${idPrefix}-rule`} value={form.ruleType} onChange={(e) => update({ ruleType: e.target.value as BadgeForm['ruleType'] })}>
            {BADGE_RULE_TYPES.map((type) => (
              <option key={type} value={type}>
                {t(RULE_TYPE_LABEL[type])}
              </option>
            ))}
          </NativeSelect>
        </Field>
        <Field>
          <Label htmlFor={`${idPrefix}-threshold`}>{t(THRESHOLD_LABEL[form.ruleType])}</Label>
          <Input
            id={`${idPrefix}-threshold`}
            type="number"
            inputMode="numeric"
            min={1}
            className="h-10 bg-card"
            value={form.threshold}
            aria-invalid={errors.threshold ? true : undefined}
            aria-describedby={errors.threshold ? errorId('threshold') : undefined}
            onChange={(e) => update({ threshold: e.target.value })}
          />
          {errors.threshold && (
            <p id={errorId('threshold')} role="alert" className="text-sm font-semibold text-destructive">
              {t(errors.threshold)}
            </p>
          )}
        </Field>
      </div>

      {form.ruleType === 'onTimeWeeks' ? (
        <p className="rounded-xl bg-accent/60 px-3 py-2 text-sm">{t('badges.onTimeNote')}</p>
      ) : (
        <fieldset className="flex flex-col gap-3">
          <legend className="text-sm font-semibold">{t('badges.tasks')}</legend>
          <p className="text-sm text-muted-foreground">{t('badges.tasksHint')}</p>
          <Input
            type="search"
            className="h-10 max-w-sm bg-card"
            aria-label={t('badges.tasksSearch')}
            placeholder={t('badges.tasksSearch')}
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          {visibleTasks.length === 0 ? (
            <p className="text-sm text-muted-foreground">{t('badges.tasksNone')}</p>
          ) : (
            <ul className="grid max-h-60 gap-1 overflow-y-auto rounded-xl border bg-card p-2 sm:grid-cols-2 lg:grid-cols-3">
              {visibleTasks.map((task) => (
                <li key={task.id}>
                  <label className="flex min-h-9 cursor-pointer items-center gap-2 rounded-lg px-2 text-sm hover:bg-secondary/60">
                    <input type="checkbox" className={checkboxClass} checked={form.taskIds.includes(task.id)} onChange={() => toggleTask(task.id)} />
                    <span className="min-w-0 [overflow-wrap:anywhere]">{task.name}</span>
                  </label>
                </li>
              ))}
            </ul>
          )}
        </fieldset>
      )}

      <fieldset className="flex flex-col gap-3">
        <legend className="text-sm font-semibold">{t('badges.image')}</legend>
        <div className="flex flex-wrap items-center gap-4">
          {preview ? (
            <img src={preview} alt={t('badges.imagePreview')} width={56} height={56} className="size-14 shrink-0 rounded-xl bg-secondary object-cover" />
          ) : (
            <BadgeImage badge={{ name: nameOfBadge, image: null }} />
          )}
          <div className="flex min-w-0 flex-col gap-2">
            <div className="flex flex-wrap items-center gap-2">
              <Button type="button" variant="outline" size="sm" onClick={() => fileInput.current?.click()}>
                <ImagePlus aria-hidden="true" />
                {t('badges.imageChoose')}
              </Button>
              {preview && (
                <Button type="button" variant="ghost" size="sm" onClick={removeImage}>
                  <X aria-hidden="true" />
                  {t('badges.imageRemove')}
                </Button>
              )}
              {form.image.kind === 'new' && <span className="text-sm text-muted-foreground [overflow-wrap:anywhere]">{form.image.fileName}</span>}
            </div>
            <input
              ref={fileInput}
              id={`${idPrefix}-image`}
              type="file"
              accept={limits.imageTypes.join(',')}
              className="visually-hidden"
              tabIndex={-1}
              aria-label={t('badges.imageChoose')}
              onChange={(e) => void chooseImage(e.target.files?.[0])}
            />
            <p className="text-sm text-muted-foreground">{preview ? t('badges.imageHint') : `${t('badges.imageHint')} ${t('badges.imageDefault')}`}</p>
            {errors.image && (
              <p role="alert" className="text-sm font-semibold text-destructive">
                {t(errors.image)}
              </p>
            )}
          </div>
        </div>
      </fieldset>

      <label className="flex w-fit cursor-pointer items-center gap-2 text-sm font-semibold">
        <input type="checkbox" className={checkboxClass} checked={form.active} onChange={(e) => update({ active: e.target.checked })} />
        {t('badges.active')}
      </label>

      {failure && <FormMessage kind="alert">{failure}</FormMessage>}
      <FormActions>
        <Button type="button" variant="ghost" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button type="submit" disabled={save.isPending}>
          <Save aria-hidden="true" />
          {t('common.save')}
        </Button>
      </FormActions>
    </form>
  );
}

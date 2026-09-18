import type { CreateTaskInput, Task, User } from '@huishoudplanner/shared';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router';
import {
  Archive,
  ArchiveRestore,
  ChevronsDownUp,
  ChevronsUpDown,
  ChevronDown,
  ChevronRight,
  FileDown,
  History,
  House,
  Pencil,
  Plus,
  Trash2,
  UserRound,
  Users,
} from 'lucide-react';
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
import { cn } from '@/lib/utils';
import { api, ApiRequestError } from '../../api/index.ts';
import { queryKeys, useRooms, useSettings, useTasks } from '../../api/queries.ts';
import { format, t } from '../../i18n/nl.ts';
import { getLanguage } from '../../i18n/runtime.ts';
import { Avatar } from '../../identity/Avatar.tsx';
import { useProfile } from '../../identity/index.ts';
import { AiPage } from '../ai/AiPage.tsx';
import { groupTasksByRoom, type RoomGroup } from './groupTasks.ts';
import { TaskForm } from './TaskForm.tsx';
import {
  emptyTaskForm,
  taskToForm,
  toTaskInput,
  type TaskFormErrors,
  type TaskFormValues,
} from './taskForm.ts';

type Editing = { mode: 'new'; roomId?: string } | { mode: 'edit'; task: Task } | null;

const FORM_FIELDS = new Set<keyof TaskFormValues>([
  'name',
  'roomId',
  'intervalKey',
  'durationMinutes',
  'defaultAssigneeId',
]);

function serverFieldErrors(error: unknown): TaskFormErrors {
  if (
    !(error instanceof ApiRequestError) ||
    error.code !== 'validation_error' ||
    !Array.isArray(error.details)
  ) {
    return {};
  }
  const errors: TaskFormErrors = {};
  for (const issue of error.details as { field?: string }[]) {
    const field = issue.field as keyof TaskFormValues | undefined;
    if (field && FORM_FIELDS.has(field)) errors[field] = 'tasks.error.server';
  }
  return errors;
}

export function TasksPage() {
  const queryClient = useQueryClient();
  const rooms = useRooms();
  const tasks = useTasks();
  const settings = useSettings();
  const { activeUsers } = useProfile();
  const [showInactive, setShowInactive] = useState(false);
  const [roomFilter, setRoomFilter] = useState('all');
  const [editing, setEditing] = useState<Editing>(null);
  const [deleting, setDeleting] = useState<Task | null>(null);
  const [collapsedRooms, setCollapsedRooms] = useState<Set<string> | null>(null);

  const invalidateTasks = () => queryClient.invalidateQueries({ queryKey: queryKeys.tasks });

  const saveTask = useMutation({
    mutationFn: async ({ id, input }: { id?: string; input: CreateTaskInput }) =>
      id
        ? (await api.patch<Task>(`/api/tasks/${id}`, input)).data
        : (await api.post<Task>('/api/tasks', input)).data,
    onSuccess: async () => {
      setEditing(null);
      await invalidateTasks();
    },
  });

  const setActive = useMutation({
    mutationFn: async ({ id, active }: { id: string; active: boolean }) =>
      (await api.patch<Task>(`/api/tasks/${id}`, { active })).data,
    onSuccess: invalidateTasks,
  });

  const bulk = useMutation({
    mutationFn: async ({ roomId, body }: { roomId: string; body: Record<string, unknown> }) =>
      (await api.post<{ updated: number }>(`/api/rooms/${roomId}/tasks/bulk`, body)).data,
    onSuccess: invalidateTasks,
  });

  const removeTask = useMutation({
    mutationFn: async (id: string) => (await api.delete<{ deleted: boolean }>(`/api/tasks/${id}`)).data,
    onSuccess: async () => {
      setDeleting(null);
      await Promise.all([
        invalidateTasks(),
        queryClient.invalidateQueries({ queryKey: ['cycle-plans'] }),
      ]);
    },
  });

  if (rooms.isPending || tasks.isPending || settings.isPending) {
    return (
      <p role="status" className="text-muted-foreground">
        {t('app.loading')}
      </p>
    );
  }
  if (rooms.isError || tasks.isError || settings.isError) {
    return (
      <p role="alert" className="rounded-xl bg-destructive/10 p-4 text-destructive">
        {t('app.error')}
      </p>
    );
  }

  const intervals = settings.data.intervals;
  const intervalLabel = (key: string) => intervals.find((i) => i.key === key)?.label ?? key;
  const userName = (id: string | null) =>
    id === null
      ? t('tasks.anyone')
      : (activeUsers.find((u) => u._id === id)?.name ?? t('tasks.unknownUser'));
  const visible = tasks.data.filter(
    (task) => (showInactive || task.active) && (roomFilter === 'all' || task.roomId === roomFilter),
  );
  const filteredRooms = roomFilter === 'all' ? rooms.data : rooms.data.filter((room) => room._id === roomFilter);
  const groups = groupTasksByRoom(visible, filteredRooms);
  const activeRooms = rooms.data.filter((r) => r.active);
  const editingTitle =
    editing?.mode === 'edit'
      ? format('tasks.editTitle', { name: editing.task.name })
      : t('tasks.new');

  const submit = (values: TaskFormValues) => {
    saveTask.mutate({
      id: editing?.mode === 'edit' ? editing.task._id : undefined,
      input: toTaskInput(values),
    });
  };

  return (
    <section className="flex flex-col gap-6">
      <PageHeader
        className="mb-0"
        title={t('nav.tasks')}
        actions={
          <>
            <Button
              type="button"
              variant="outline"
              onClick={() => setCollapsedRooms(new Set(groups.map((group) => group.roomId)))}
            >
              <ChevronsDownUp aria-hidden="true" />
              {t('tasks.collapseAll')}
            </Button>
            <Button type="button" variant="outline" onClick={() => setCollapsedRooms(new Set())}>
              <ChevronsUpDown aria-hidden="true" />
              {t('tasks.expandAll')}
            </Button>
            <NativeSelect
              className="w-48"
              value={roomFilter}
              onChange={(event) => setRoomFilter(event.target.value)}
              aria-label={t('tasks.filterRoom')}
            >
              <option value="all">{t('tasks.allRooms')}</option>
              {rooms.data.map((room) => (
                <option key={room._id} value={room._id}>{room.name}</option>
              ))}
            </NativeSelect>
            <label className="inline-flex h-9 cursor-pointer items-center gap-2 rounded-lg px-3 text-sm font-semibold text-muted-foreground hover:bg-secondary">
              <input
                type="checkbox"
                className="size-4 accent-primary"
                checked={showInactive}
                onChange={(e) => setShowInactive(e.target.checked)}
              />
              {t('tasks.showInactive')}
            </label>
            <Button
              type="button"
              onClick={() => {
                saveTask.reset();
                setEditing({ mode: 'new' });
              }}
            >
              <Plus aria-hidden="true" />
              {t('tasks.new')}
            </Button>
            <Button asChild variant="outline">
              <a href={getLanguage() === 'en' ? '/api/export/pdf/tasks?language=en' : '/api/export/pdf/tasks'} download>
                <FileDown aria-hidden="true" />
                {t('tasks.exportPdf')}
              </a>
            </Button>
          </>
        }
      />

      <Dialog
        open={editing !== null}
        onOpenChange={(open) => {
          if (!open && !saveTask.isPending) setEditing(null);
        }}
      >
        {editing && (
          <DialogContent className="max-h-[calc(100dvh-2rem)] overflow-y-auto sm:max-w-2xl">
            <DialogHeader className="pr-8">
              <DialogTitle>{editingTitle}</DialogTitle>
              <DialogDescription>
                Vul de taakgegevens in. Je plek in de takenlijst blijft behouden.
              </DialogDescription>
            </DialogHeader>
            <TaskForm
              key={editing.mode === 'edit' ? editing.task._id : `new-${editing.roomId ?? ''}`}
              title={editingTitle}
              showTitle={false}
              className="rounded-none border-0 bg-transparent p-0 shadow-none"
              initial={
                editing.mode === 'edit'
                  ? taskToForm(editing.task)
                  : emptyTaskForm({ roomId: editing.roomId ?? '' })
              }
              rooms={activeRooms}
              intervals={intervals}
              users={activeUsers}
              submitting={saveTask.isPending}
              serverErrors={serverFieldErrors(saveTask.error)}
              onSubmit={submit}
              onCancel={() => setEditing(null)}
            />
          </DialogContent>
        )}
      </Dialog>
      <Dialog open={deleting !== null} onOpenChange={(open) => !open && !removeTask.isPending && setDeleting(null)}>
        {deleting && (
          <DialogContent>
            <DialogHeader>
              <DialogTitle>{format('tasks.deleteConfirmTitle', { name: deleting.name })}</DialogTitle>
              <DialogDescription>{t('tasks.deleteConfirmBody')}</DialogDescription>
            </DialogHeader>
            {removeTask.isError && <p role="alert" className="text-sm text-destructive">{t('tasks.deleteError')}</p>}
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setDeleting(null)}>{t('common.cancel')}</Button>
              <Button type="button" variant="destructive" disabled={removeTask.isPending} onClick={() => removeTask.mutate(deleting._id)}>
                <Trash2 aria-hidden="true" />{t('tasks.delete')}
              </Button>
            </DialogFooter>
          </DialogContent>
        )}
      </Dialog>
      {saveTask.isError &&
        !(
          saveTask.error instanceof ApiRequestError && saveTask.error.code === 'validation_error'
        ) && (
          <p role="alert" className="rounded-xl bg-destructive/10 p-4 text-destructive">
            {t('app.error')}
          </p>
        )}

      <div className="grid items-start gap-6 xl:grid-cols-[minmax(0,1fr)_22rem]">
        <div className="flex min-w-0 flex-col gap-6">
          {groups.map((group) => (
            <RoomSection
              key={group.roomId}
              group={group}
              collapsed={collapsedRooms === null || collapsedRooms.has(group.roomId)}
              onCollapsedChange={(collapsed) =>
                setCollapsedRooms((current) => {
                  const next = current
                    ? new Set(current)
                    : new Set(groups.map((item) => item.roomId));
                  if (collapsed) next.add(group.roomId);
                  else next.delete(group.roomId);
                  return next;
                })
              }
              users={activeUsers}
              intervalLabel={intervalLabel}
              userName={userName}
              onAdd={() => {
                saveTask.reset();
                setEditing({ mode: 'new', roomId: group.room?.active ? group.roomId : undefined });
              }}
              onEdit={(task) => {
                saveTask.reset();
                setEditing({ mode: 'edit', task });
              }}
              onToggleActive={(task) => setActive.mutate({ id: task._id, active: !task.active })}
              onDelete={setDeleting}
              onBulkDeactivate={() => {
                if (
                  window.confirm(
                    format('tasks.bulk.confirmDeactivate', { room: group.room?.name ?? '' }),
                  )
                ) {
                  bulk.mutate({ roomId: group.roomId, body: { op: 'deactivate' } });
                }
              }}
              onBulkReassign={(assigneeId) =>
                bulk.mutate({
                  roomId: group.roomId,
                  body: { op: 'reassign', defaultAssigneeId: assigneeId },
                })
              }
            />
          ))}
        </div>
        <aside
          aria-label={t('settings.ai.title')}
          className="min-w-0 xl:sticky xl:top-6"
        >
          <AiPage section="tasks" embedded />
        </aside>
      </div>
    </section>
  );
}

interface RoomSectionProps {
  group: RoomGroup;
  collapsed: boolean;
  onCollapsedChange(collapsed: boolean): void;
  users: User[];
  intervalLabel(key: string): string;
  userName(id: string | null): string;
  onAdd(): void;
  onEdit(task: Task): void;
  onToggleActive(task: Task): void;
  onDelete(task: Task): void;
  onBulkDeactivate(): void;
  onBulkReassign(assigneeId: string | null): void;
}

function RoomSection({
  group,
  collapsed,
  onCollapsedChange,
  users,
  intervalLabel,
  userName,
  onAdd,
  onEdit,
  onToggleActive,
  onDelete,
  onBulkDeactivate,
  onBulkReassign,
}: RoomSectionProps) {
  const [assignee, setAssignee] = useState('');
  const roomName = group.room?.name ?? t('tasks.unknownRoom');
  const hasActiveTasks = group.tasks.some((task) => task.active);
  const headingId = `room-${group.roomId}`;

  return (
    <section
      className="overflow-hidden rounded-2xl border bg-card shadow-sm"
      aria-labelledby={headingId}
    >
      <div className="flex flex-wrap items-center gap-3 px-6 pt-5 pb-4">
        <Button
          type="button"
          variant="ghost"
          size="icon-sm"
          aria-label={format(collapsed ? 'tasks.expandRoom' : 'tasks.collapseRoom', { room: roomName })}
          aria-expanded={!collapsed}
          onClick={() => onCollapsedChange(!collapsed)}
        >
          {collapsed ? <ChevronRight aria-hidden="true" /> : <ChevronDown aria-hidden="true" />}
        </Button>
        <span className="grid size-9 place-items-center rounded-xl bg-accent text-accent-foreground">
          <House className="size-5" aria-hidden="true" />
        </span>
        <h2 id={headingId} className="flex items-center gap-2">
          {roomName}
          {group.room && !group.room.active && (
            <Badge variant="outline" className="text-muted-foreground">
              {' '}
              {t('tasks.inactive')}
            </Badge>
          )}
        </h2>
        <Badge variant="secondary" className="tabular-nums">
          {group.tasks.length}
        </Badge>
        {group.room?.active && (
          <Button type="button" variant="outline" size="sm" className="ml-auto" onClick={onAdd}>
            <Plus aria-hidden="true" />
            {format('tasks.addToRoom', { room: roomName })}
          </Button>
        )}
      </div>

      {!collapsed && hasActiveTasks && group.room && (
        <div
          className="flex flex-wrap items-center gap-2 border-y bg-secondary/40 px-6 py-2"
          role="group"
          aria-label={format('tasks.bulk.label', { room: roomName })}
        >
          <Button type="button" variant="ghost" size="sm" onClick={onBulkDeactivate}>
            <Archive aria-hidden="true" />
            {t('tasks.bulk.deactivate')}
          </Button>
          <span className="ml-auto flex items-center gap-2">
            <NativeSelect
              className="w-44"
              value={assignee}
              onChange={(e) => setAssignee(e.target.value)}
              aria-label={t('tasks.bulk.reassignTo')}
            >
              <option value="">{t('tasks.anyone')}</option>
              {users.map((user) => (
                <option key={user._id} value={user._id}>
                  {user.name}
                </option>
              ))}
            </NativeSelect>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => onBulkReassign(assignee || null)}
            >
              <UserRound aria-hidden="true" />
              {t('tasks.bulk.reassign')}
            </Button>
          </span>
        </div>
      )}

      {!collapsed && (group.tasks.length === 0 ? (
        <p className="px-6 pb-5 text-sm text-muted-foreground">{t('tasks.emptyRoom')}</p>
      ) : (
        <ul className={cn('divide-y', !(hasActiveTasks && group.room) && 'border-t')}>
          {group.tasks.map((task) => {
            const assignee = users.find((u) => u._id === task.defaultAssigneeId);
            return (
              <li
                key={task._id}
                className={cn(
                  'flex items-center gap-4 px-6 py-3 transition-colors hover:bg-secondary/30',
                  !task.active && 'bg-muted/40',
                )}
              >
                {assignee ? (
                  <Avatar name={assignee.name} color={assignee.color} size="sm" />
                ) : (
                  <span className="grid size-7 shrink-0 place-items-center rounded-full bg-secondary text-secondary-foreground">
                    <Users className="size-3.5" aria-hidden="true" />
                  </span>
                )}
                <div className="flex min-w-0 flex-1 flex-col gap-1">
                  <span className="flex items-center gap-2">
                    <strong
                      className={cn(
                        'truncate font-bold',
                        !task.active && 'font-normal text-muted-foreground italic',
                      )}
                    >
                      {task.name}
                    </strong>
                    {!task.active && (
                      <Badge variant="outline" className="text-muted-foreground">
                        {' '}
                        {t('tasks.inactive')}
                      </Badge>
                    )}
                  </span>
                  <span className="flex flex-wrap items-center gap-1 text-sm text-muted-foreground">
                    <Badge variant="secondary">{intervalLabel(task.intervalKey)}</Badge>
                    <span aria-hidden="true">{' · '}</span>
                    <span className="tabular-nums">
                      {format('tasks.minutes', { minutes: task.durationMinutes })}
                    </span>
                    <span aria-hidden="true">{' · '}</span>
                    <span>{userName(task.defaultAssigneeId)}</span>
                  </span>
                </div>
                <div className="flex shrink-0 items-center gap-1">
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    onClick={() => onEdit(task)}
                    aria-label={format('tasks.editTitle', { name: task.name })}
                  >
                    <Pencil aria-hidden="true" />
                    {t('common.edit')}
                  </Button>
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    onClick={() => onToggleActive(task)}
                    aria-label={format(
                      task.active ? 'tasks.deactivateNamed' : 'tasks.activateNamed',
                      {
                        name: task.name,
                      },
                    )}
                  >
                    {task.active ? (
                      <Archive aria-hidden="true" />
                    ) : (
                      <ArchiveRestore aria-hidden="true" />
                    )}
                    {task.active ? t('tasks.deactivate') : t('tasks.activate')}
                  </Button>
                  <Button asChild variant="link" size="sm" className="text-muted-foreground">
                    <Link to={`/history?entity=task&entityId=${task._id}`}>
                      <History aria-hidden="true" />
                      {t('tasks.history')}
                    </Link>
                  </Button>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon-sm"
                    className="text-destructive hover:text-destructive"
                    aria-label={format('tasks.deleteNamed', { name: task.name })}
                    onClick={() => onDelete(task)}
                  >
                    <Trash2 aria-hidden="true" />
                  </Button>
                </div>
              </li>
            );
          })}
        </ul>
      ))}
    </section>
  );
}

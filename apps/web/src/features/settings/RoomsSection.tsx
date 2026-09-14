import type { Room } from '@huishoudplanner/shared';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { DoorOpen, Pencil, Plus, Save, Trash2 } from 'lucide-react';
import { useId, useState, type FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { cn } from '@/lib/utils';
import { api } from '../../api/index.ts';
import { queryKeys, useRooms, useTasks } from '../../api/queries.ts';
import { format, t } from '../../i18n/nl.ts';
import { getLocale } from '../../i18n/runtime.ts';
import {
  checkboxClass,
  Field,
  FormActions,
  FormMessage,
  listRowClass,
  SettingsCardHeader,
  settingsCardClass,
} from './SettingsCard.tsx';

const byOrder = (a: Room, b: Room) =>
  a.sortOrder - b.sortOrder || a.name.localeCompare(b.name, getLocale());

export function RoomsSection() {
  const idPrefix = useId();
  const rooms = useRooms();
  const tasks = useTasks();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<Room | null>(null);
  const [saved, setSaved] = useState(false);

  const open = (id: string | null) => {
    setSaved(false);
    setEditing(id);
  };
  const done = () => {
    setEditing(null);
    setSaved(true);
  };

  const removeRoom = useMutation({
    mutationFn: async (id: string) => (await api.delete<{ deleted: boolean }>(`/api/rooms/${id}`)).data,
    onSuccess: async () => {
      setDeleting(null);
      await queryClient.invalidateQueries({ queryKey: queryKeys.rooms });
    },
  });

  return (
    <section className={settingsCardClass} aria-labelledby={`${idPrefix}-title`}>
      <SettingsCardHeader icon={<DoorOpen aria-hidden="true" />} titleId={`${idPrefix}-title`} title={t('settings.rooms.title')} />
      {rooms.isPending || tasks.isPending ? (
        <p className="text-sm text-muted-foreground">{t('app.loading')}</p>
      ) : rooms.isError || tasks.isError ? (
        <FormMessage kind="alert">{t('app.error')}</FormMessage>
      ) : (
        <ul className="flex flex-col gap-2">
          {[...rooms.data].sort(byOrder).map((room) => {
            const taskCount = tasks.data.filter((task) => task.roomId === room._id).length;
            return (
            <li key={room._id}>
              {editing === room._id ? (
                <RoomForm room={room} onSaved={done} onCancel={() => open(null)} />
              ) : (
                <div className={cn(listRowClass, !room.active && 'opacity-70')}>
                  <span className="grid h-7 min-w-7 place-items-center rounded-lg bg-secondary px-1.5 text-xs font-bold text-secondary-foreground tabular-nums">
                    {room.sortOrder}
                  </span>
                  <strong className="font-bold">{room.name}</strong>
                  {!room.active && <span className="text-sm text-muted-foreground">({t('settings.rooms.inactive')})</span>}
                  {taskCount > 0 && <span className="text-sm text-muted-foreground">{format('settings.rooms.taskCount', { count: taskCount })}</span>}
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    className="ml-auto"
                    aria-label={format('settings.rooms.edit', { name: room.name })}
                    onClick={() => open(room._id)}
                  >
                    <Pencil aria-hidden="true" />
                    {t('common.edit')}
                  </Button>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon-sm"
                    className="text-destructive hover:text-destructive"
                    disabled={taskCount > 0}
                    title={taskCount > 0 ? t('settings.rooms.deleteBlocked') : undefined}
                    aria-label={format('settings.rooms.delete', { name: room.name })}
                    onClick={() => setDeleting(room)}
                  >
                    <Trash2 aria-hidden="true" />
                  </Button>
                </div>
              )}
            </li>
            );
          })}
        </ul>
      )}
      {editing === 'new' ? (
        <RoomForm onSaved={done} onCancel={() => open(null)} />
      ) : (
        <div>
          <Button type="button" variant="outline" onClick={() => open('new')}>
            <Plus aria-hidden="true" />
            {t('settings.rooms.add')}
          </Button>
        </div>
      )}
      {saved && <FormMessage kind="status">{t('settings.saved')}</FormMessage>}
      <Dialog open={deleting !== null} onOpenChange={(open) => !open && !removeRoom.isPending && setDeleting(null)}>
        {deleting && (
          <DialogContent>
            <DialogHeader>
              <DialogTitle>{format('settings.rooms.deleteConfirmTitle', { name: deleting.name })}</DialogTitle>
              <DialogDescription>{t('settings.rooms.deleteConfirmBody')}</DialogDescription>
            </DialogHeader>
            {removeRoom.isError && <FormMessage kind="alert">{t('settings.rooms.deleteError')}</FormMessage>}
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setDeleting(null)}>{t('common.cancel')}</Button>
              <Button type="button" variant="destructive" disabled={removeRoom.isPending} onClick={() => removeRoom.mutate(deleting._id)}>
                <Trash2 aria-hidden="true" />{t('settings.rooms.deleteConfirm')}
              </Button>
            </DialogFooter>
          </DialogContent>
        )}
      </Dialog>
    </section>
  );
}

function RoomForm({ room, onSaved, onCancel }: { room?: Room; onSaved(): void; onCancel(): void }) {
  const idPrefix = useId();
  const queryClient = useQueryClient();
  const [name, setName] = useState(room?.name ?? '');
  const [sortOrder, setSortOrder] = useState(room ? String(room.sortOrder) : '');
  const [active, setActive] = useState(room?.active ?? true);
  const [error, setError] = useState<string | null>(null);

  const save = useMutation({
    mutationFn: async () => {
      // A new room without a position goes to the end (server default).
      const order = sortOrder.trim() === '' ? {} : { sortOrder: Number(sortOrder) };
      return room
        ? api.patch(`/api/rooms/${room._id}`, { name: name.trim(), ...order, active })
        : api.post('/api/rooms', { name: name.trim(), ...order });
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.rooms });
      onSaved();
    },
    onError: () => setError(t('app.error')),
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!name.trim()) return setError(t('settings.rooms.nameRequired'));
    if (sortOrder.trim() !== '' && !Number.isInteger(Number(sortOrder))) return setError(t('settings.rooms.sortOrderInvalid'));
    setError(null);
    save.mutate();
  };

  return (
    <form
      className="flex flex-col gap-5 rounded-xl border border-primary/25 bg-secondary/40 p-5"
      onSubmit={submit}
      noValidate
      aria-label={room ? format('settings.rooms.edit', { name: room.name }) : t('settings.rooms.new')}
    >
      <div className="grid gap-4 sm:grid-cols-[1fr_8rem]">
        <Field>
          <Label htmlFor={`${idPrefix}-name`}>{t('settings.rooms.name')}</Label>
          <Input id={`${idPrefix}-name`} className="h-10 bg-card" value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field>
          <Label htmlFor={`${idPrefix}-order`}>{t('settings.rooms.sortOrder')}</Label>
          <Input
            id={`${idPrefix}-order`}
            type="number"
            className="h-10 bg-card"
            value={sortOrder}
            onChange={(e) => setSortOrder(e.target.value)}
          />
        </Field>
      </div>
      {room && (
        <label className="flex w-fit cursor-pointer items-center gap-2 text-sm font-semibold">
          <input type="checkbox" className={checkboxClass} checked={active} onChange={(e) => setActive(e.target.checked)} />
          {t('settings.rooms.active')}
        </label>
      )}
      {error && <FormMessage kind="alert">{error}</FormMessage>}
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

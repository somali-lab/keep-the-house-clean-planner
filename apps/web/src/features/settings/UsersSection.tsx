import type { User } from '@huishoudplanner/shared';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Pencil, Plus, Save, Users } from 'lucide-react';
import { useId, useState, type CSSProperties, type FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { cn } from '@/lib/utils';
import { api } from '../../api/index.ts';
import { useUsers } from '../../api/queries.ts';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import {
  checkboxClass,
  Field,
  FormActions,
  FormMessage,
  listRowClass,
  SettingsCardHeader,
  settingsCardClass,
} from './SettingsCard.tsx';

/** Monday first; values are 0=Sunday..6=Saturday as stored. */
const WEEKDAYS = [1, 2, 3, 4, 5, 6, 0];

const isBudget = (value: string) => value.trim() !== '' && Number.isInteger(Number(value)) && Number(value) >= 0;

export function UsersSection() {
  const idPrefix = useId();
  const users = useUsers();
  const [editing, setEditing] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const open = (id: string | null) => {
    setSaved(false);
    setEditing(id);
  };
  const done = () => {
    setEditing(null);
    setSaved(true);
  };

  return (
    <section className={settingsCardClass} aria-labelledby={`${idPrefix}-title`}>
      <SettingsCardHeader icon={<Users aria-hidden="true" />} titleId={`${idPrefix}-title`} title={t('settings.users.title')} />
      {users.isPending ? (
        <p className="text-sm text-muted-foreground">{t('app.loading')}</p>
      ) : users.isError ? (
        <FormMessage kind="alert">{t('app.error')}</FormMessage>
      ) : (
        <ul className="flex flex-col gap-2">
          {users.data.map((user) => (
            <li key={user._id}>
              {editing === user._id ? (
                <UserForm user={user} onSaved={done} onCancel={() => open(null)} />
              ) : (
                <div className={cn(listRowClass, !user.active && 'opacity-70')}>
                  <span
                    className="user-color size-8 shrink-0 rounded-full shadow-sm ring-2 ring-card"
                    style={{ '--user-color': user.color } as CSSProperties}
                    aria-hidden="true"
                  />
                  <strong className="font-bold">{user.name}</strong>
                  {!user.active && <span className="text-sm text-muted-foreground">({t('settings.users.inactive')})</span>}
                  <span className="text-sm text-muted-foreground">{format('settings.users.summary', user.dailyBudgetMinutes)}</span>
                  <span className="text-sm text-muted-foreground">
                    {format('settings.users.maxDailySummary', user.maxDailyMinutes ?? { weekday: 60, weekend: 120 })}
                  </span>
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    className="ml-auto"
                    aria-label={format('settings.users.edit', { name: user.name })}
                    onClick={() => open(user._id)}
                  >
                    <Pencil aria-hidden="true" />
                    {t('common.edit')}
                  </Button>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
      {editing === 'new' ? (
        <UserForm onSaved={done} onCancel={() => open(null)} />
      ) : (
        <div>
          <Button type="button" variant="outline" onClick={() => open('new')}>
            <Plus aria-hidden="true" />
            {t('settings.users.add')}
          </Button>
        </div>
      )}
      {saved && <FormMessage kind="status">{t('settings.saved')}</FormMessage>}
    </section>
  );
}

function UserForm({ user, onSaved, onCancel }: { user?: User; onSaved(): void; onCancel(): void }) {
  const idPrefix = useId();
  const queryClient = useQueryClient();
  const [name, setName] = useState(user?.name ?? '');
  const [color, setColor] = useState(user?.color ?? '#2563eb');
  const [active, setActive] = useState(user?.active ?? true);
  const [unavailable, setUnavailable] = useState<number[]>(user?.unavailableWeekdays ?? []);
  const [weekday, setWeekday] = useState(String(user?.dailyBudgetMinutes.weekday ?? 60));
  const [weekend, setWeekend] = useState(String(user?.dailyBudgetMinutes.weekend ?? 120));
  const [maxWeekday, setMaxWeekday] = useState(String(user?.maxDailyMinutes?.weekday ?? 60));
  const [maxWeekend, setMaxWeekend] = useState(String(user?.maxDailyMinutes?.weekend ?? 120));
  const [error, setError] = useState<string | null>(null);

  const save = useMutation({
    mutationFn: async () => {
      const body = {
        name: name.trim(),
        color,
        unavailableWeekdays: [...unavailable].sort((a, b) => a - b),
        dailyBudgetMinutes: { weekday: Number(weekday), weekend: Number(weekend) },
        maxDailyMinutes: { weekday: Number(maxWeekday), weekend: Number(maxWeekend) },
      };
      return user ? api.patch(`/api/users/${user._id}`, { ...body, active }) : api.post('/api/users', body);
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['users'] });
      onSaved();
    },
    onError: () => setError(t('app.error')),
  });

  const toggleDay = (day: number, checked: boolean) =>
    setUnavailable((current) => (checked ? [...current, day] : current.filter((d) => d !== day)));

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!name.trim()) return setError(t('settings.users.nameRequired'));
    if (![weekday, weekend, maxWeekday, maxWeekend].every(isBudget)) return setError(t('settings.users.budgetInvalid'));
    setError(null);
    save.mutate();
  };

  return (
    <form
      className="flex flex-col gap-5 rounded-xl border border-primary/25 bg-secondary/40 p-5"
      onSubmit={submit}
      noValidate
      aria-label={user ? format('settings.users.edit', { name: user.name }) : t('settings.users.new')}
    >
      <div className="grid gap-4 sm:grid-cols-[1fr_auto]">
        <Field>
          <Label htmlFor={`${idPrefix}-name`}>{t('settings.users.name')}</Label>
          <Input id={`${idPrefix}-name`} className="h-10 bg-card" value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field>
          <Label htmlFor={`${idPrefix}-color`}>{t('settings.users.color')}</Label>
          <Input
            id={`${idPrefix}-color`}
            type="color"
            className="h-10 w-20 cursor-pointer bg-card p-1"
            value={color}
            onChange={(e) => setColor(e.target.value)}
          />
        </Field>
      </div>
      {user && (
        <label className="flex w-fit cursor-pointer items-center gap-2 text-sm font-semibold">
          <input type="checkbox" className={checkboxClass} checked={active} onChange={(e) => setActive(e.target.checked)} />
          {t('settings.users.active')}
        </label>
      )}
      <fieldset className="flex flex-col gap-2">
        <legend className="mb-2 text-sm font-medium">{t('settings.users.unavailable')}</legend>
        <div className="flex flex-wrap gap-2">
          {WEEKDAYS.map((day) => (
            <label
              key={day}
              className="cursor-pointer rounded-full border bg-card px-3 py-1.5 text-sm font-semibold transition-colors select-none hover:bg-secondary has-[:checked]:border-primary has-[:checked]:bg-primary has-[:checked]:text-primary-foreground has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring/50"
            >
              <input
                type="checkbox"
                className="sr-only"
                checked={unavailable.includes(day)}
                onChange={(e) => toggleDay(day, e.target.checked)}
              />
              {t(`weekdayLong.${day}` as MessageKey)}
            </label>
          ))}
        </div>
      </fieldset>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field>
          <Label htmlFor={`${idPrefix}-weekday`}>{t('settings.users.budgetWeekday')}</Label>
          <Input
            id={`${idPrefix}-weekday`}
            type="number"
            min="0"
            className="h-10 bg-card"
            value={weekday}
            onChange={(e) => setWeekday(e.target.value)}
          />
        </Field>
        <Field>
          <Label htmlFor={`${idPrefix}-weekend`}>{t('settings.users.budgetWeekend')}</Label>
          <Input
            id={`${idPrefix}-weekend`}
            type="number"
            min="0"
            className="h-10 bg-card"
            value={weekend}
            onChange={(e) => setWeekend(e.target.value)}
          />
        </Field>
      </div>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field>
          <Label htmlFor={`${idPrefix}-max-weekday`}>{t('settings.users.maxDailyWeekday')}</Label>
          <Input
            id={`${idPrefix}-max-weekday`}
            type="number"
            min="0"
            className="h-10 bg-card"
            value={maxWeekday}
            onChange={(e) => setMaxWeekday(e.target.value)}
          />
        </Field>
        <Field>
          <Label htmlFor={`${idPrefix}-max-weekend`}>{t('settings.users.maxDailyWeekend')}</Label>
          <Input
            id={`${idPrefix}-max-weekend`}
            type="number"
            min="0"
            className="h-10 bg-card"
            value={maxWeekend}
            onChange={(e) => setMaxWeekend(e.target.value)}
          />
        </Field>
      </div>
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

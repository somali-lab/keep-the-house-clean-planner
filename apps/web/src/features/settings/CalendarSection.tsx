import type { Settings, VacationRange } from '@huishoudplanner/shared';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { CalendarDays, Plus, Save, TreePalm } from 'lucide-react';
import { useId, useState, type FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { api } from '../../api/index.ts';
import { queryKeys } from '../../api/queries.ts';
import { format, t } from '../../i18n/nl.ts';
import { Field, FormActions, FormMessage, listRowClass, SettingsCardHeader, settingsCardClass } from './SettingsCard.tsx';

type Message = { kind: 'status' | 'alert'; text: string } | null;

/** Checked here for a direct message; the server validates too (anchor_not_monday). */
export function isMondayKey(key: string): boolean {
  return /^\d{4}-\d{2}-\d{2}$/.test(key) && new Date(`${key}T12:00:00Z`).getUTCDay() === 1;
}

const dmy = (key: string) => key.split('-').reverse().join('-');

/** Cycle anchor date and vacation ranges. Local state is kept after saving, so the confirmation stays visible. */
export function CalendarSection({ settings }: { settings: Settings }) {
  return (
    <>
      <AnchorForm settings={settings} />
      <VacationsForm settings={settings} />
    </>
  );
}

function AnchorForm({ settings }: { settings: Settings }) {
  const idPrefix = useId();
  const queryClient = useQueryClient();
  const [anchor, setAnchor] = useState(settings.cycleAnchorDate);
  const [message, setMessage] = useState<Message>(null);

  const save = useMutation({
    mutationFn: () => api.patch('/api/settings', { cycleAnchorDate: anchor }),
    onSuccess: async () => {
      setMessage({ kind: 'status', text: t('settings.saved') });
      await queryClient.invalidateQueries({ queryKey: queryKeys.settings });
    },
    onError: () => setMessage({ kind: 'alert', text: t('app.error') }),
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!isMondayKey(anchor)) {
      setMessage({ kind: 'alert', text: t('settings.anchor.notMonday') });
      return;
    }
    setMessage(null);
    save.mutate();
  };

  return (
    <form className={settingsCardClass} onSubmit={submit} aria-labelledby={`${idPrefix}-title`}>
      <SettingsCardHeader
        icon={<CalendarDays aria-hidden="true" />}
        titleId={`${idPrefix}-title`}
        title={t('settings.anchor.title')}
        description={t('settings.anchor.explainer')}
      />
      <Field className="max-w-xs">
        <Label htmlFor={`${idPrefix}-anchor`}>{t('settings.anchor.label')}</Label>
        <Input
          id={`${idPrefix}-anchor`}
          type="date"
          className="h-10 bg-card"
          value={anchor}
          onChange={(e) => setAnchor(e.target.value)}
        />
      </Field>
      {message && <FormMessage kind={message.kind}>{message.text}</FormMessage>}
      <FormActions className="mt-auto">
        <Button type="submit" disabled={save.isPending}>
          <Save aria-hidden="true" />
          {t('common.save')}
        </Button>
      </FormActions>
    </form>
  );
}

function VacationsForm({ settings }: { settings: Settings }) {
  const idPrefix = useId();
  const queryClient = useQueryClient();
  const [ranges, setRanges] = useState<VacationRange[]>(settings.vacationRanges);
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [message, setMessage] = useState<Message>(null);

  const save = useMutation({
    mutationFn: () => api.patch('/api/settings', { vacationRanges: ranges }),
    onSuccess: async () => {
      setMessage({ kind: 'status', text: t('settings.saved') });
      await queryClient.invalidateQueries({ queryKey: queryKeys.settings });
    },
    onError: () => setMessage({ kind: 'alert', text: t('app.error') }),
  });

  const add = () => {
    if (!from || !to) return setMessage({ kind: 'alert', text: t('settings.vacations.incomplete') });
    if (from > to) return setMessage({ kind: 'alert', text: t('settings.vacations.inverted') });
    setRanges((current) => [...current, { from, to }].sort((a, b) => a.from.localeCompare(b.from)));
    setFrom('');
    setTo('');
    setMessage(null);
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    setMessage(null);
    save.mutate();
  };

  return (
    <form className={settingsCardClass} onSubmit={submit} aria-labelledby={`${idPrefix}-title`}>
      <SettingsCardHeader
        icon={<TreePalm aria-hidden="true" />}
        titleId={`${idPrefix}-title`}
        title={t('settings.vacations.title')}
        description={t('settings.vacations.explainer')}
      />
      {ranges.length === 0 ? (
        <p className="rounded-xl border border-dashed px-4 py-3 text-sm text-muted-foreground">{t('settings.vacations.none')}</p>
      ) : (
        <ul className="flex flex-col gap-2">
          {ranges.map((range, index) => {
            const vars = { from: dmy(range.from), to: dmy(range.to) };
            return (
              <li key={`${range.from}-${range.to}-${index}`} className={listRowClass}>
                <span className="font-semibold tabular-nums">{format('settings.vacations.range', vars)}</span>
                <Button
                  type="button"
                  variant="ghost"
                  size="icon-sm"
                  className="ml-auto text-lg text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                  aria-label={format('settings.vacations.remove', vars)}
                  onClick={() => setRanges((current) => current.filter((_, i) => i !== index))}
                >
                  ×
                </Button>
              </li>
            );
          })}
        </ul>
      )}
      <div className="grid items-end gap-3 sm:grid-cols-[1fr_1fr_auto]">
        <Field>
          <Label htmlFor={`${idPrefix}-from`}>{t('settings.vacations.from')}</Label>
          <Input id={`${idPrefix}-from`} type="date" className="h-10 bg-card" value={from} onChange={(e) => setFrom(e.target.value)} />
        </Field>
        <Field>
          <Label htmlFor={`${idPrefix}-to`}>{t('settings.vacations.to')}</Label>
          <Input id={`${idPrefix}-to`} type="date" className="h-10 bg-card" value={to} onChange={(e) => setTo(e.target.value)} />
        </Field>
        <Button type="button" variant="outline" className="h-10" onClick={add}>
          <Plus aria-hidden="true" />
          {t('settings.vacations.add')}
        </Button>
      </div>
      {message && <FormMessage kind={message.kind}>{message.text}</FormMessage>}
      <FormActions>
        <Button type="submit" disabled={save.isPending}>
          <Save aria-hidden="true" />
          {t('settings.vacations.save')}
        </Button>
      </FormActions>
    </form>
  );
}

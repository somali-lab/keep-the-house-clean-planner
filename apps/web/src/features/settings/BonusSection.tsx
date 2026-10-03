import type { BonusAmounts, Settings } from '@huishoudplanner/shared';
import { bonusAmountsOn, MAX_BONUS_POINTS, MIN_BONUS_POINTS } from '@huishoudplanner/shared/bonuses';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Clock, Gift, Save } from 'lucide-react';
import { useId, useState, type FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { api, ApiRequestError } from '../../api/index.ts';
import { queryKeys } from '../../api/queries.ts';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { dayKeyInZone } from '../today/todayModel.ts';
import { Field, FormActions, FormMessage, listRowClass, SettingsCardHeader, settingsCardClass } from './SettingsCard.tsx';

type Message = { kind: 'status' | 'alert'; text: string } | null;

const FIELDS = ['weekDone', 'weekOnTime', 'cycleDone', 'cycleOnTime'] as const satisfies readonly (keyof BonusAmounts)[];

/** The amount typed in a field, or null when it is not a whole number from 0 to 1000. The server validates too. */
export function parseBonusAmount(text: string): number | null {
  if (!/^\d{1,4}$/.test(text.trim())) return null;
  const value = Number(text);
  return value >= MIN_BONUS_POINTS && value <= MAX_BONUS_POINTS ? value : null;
}

const dmy = (key: string) => key.split('-').reverse().join('-');

/**
 * The amounts of the week and cycle bonuses (ADR-0012), for administrators. Saving writes a schedule
 * row that applies from today: periods that have ended keep the amounts they had, and an amount of 0
 * turns a bonus off. The schedule rows are listed so it is visible since when the amounts apply.
 */
export function BonusSection({ settings, now }: { settings: Settings; now?: Date }) {
  const idPrefix = useId();
  const queryClient = useQueryClient();
  const { profile } = useProfile();
  const schedule = settings.bonusSchedule ?? [];
  const todayKey = dayKeyInZone(now ?? new Date(), settings.timezone);
  const inForce = bonusAmountsOn(schedule, todayKey);
  const [values, setValues] = useState<Record<keyof BonusAmounts, string>>({
    weekDone: String(inForce.weekDone),
    weekOnTime: String(inForce.weekOnTime),
    cycleDone: String(inForce.cycleDone),
    cycleOnTime: String(inForce.cycleOnTime),
  });
  const [message, setMessage] = useState<Message>(null);

  const save = useMutation({
    mutationFn: (periodBonuses: BonusAmounts) => api.patch('/api/settings', { periodBonuses }),
    onSuccess: async () => {
      setMessage({ kind: 'status', text: t('settings.saved') });
      await queryClient.invalidateQueries({ queryKey: queryKeys.settings });
    },
    onError: (error) =>
      setMessage({
        kind: 'alert',
        text: error instanceof ApiRequestError && error.code === 'bonus_schedule_conflict' ? t('settings.bonuses.conflict') : t('app.error'),
      }),
  });

  if (profile?.role !== 'admin') return null;

  // The row in force is the last one that starts on or before today.
  const current = [...schedule].filter((row) => row.from <= todayKey).sort((a, b) => b.from.localeCompare(a.from))[0];

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const parsed = FIELDS.map((field) => [field, parseBonusAmount(values[field])] as const);
    if (parsed.some(([, amount]) => amount === null)) {
      setMessage({ kind: 'alert', text: t('settings.bonuses.invalid') });
      return;
    }
    setMessage(null);
    save.mutate(Object.fromEntries(parsed) as unknown as BonusAmounts);
  };

  return (
    <form className={settingsCardClass} onSubmit={submit} noValidate aria-labelledby={`${idPrefix}-title`}>
      <SettingsCardHeader
        icon={<Gift aria-hidden="true" />}
        titleId={`${idPrefix}-title`}
        title={t('settings.bonuses.title')}
        description={t('settings.bonuses.explainer')}
      />
      <div className="grid gap-4 sm:grid-cols-2">
        {FIELDS.map((field) => (
          <Field key={field}>
            <Label htmlFor={`${idPrefix}-${field}`}>{t(`settings.bonuses.${field}` as MessageKey)}</Label>
            <Input
              id={`${idPrefix}-${field}`}
              type="number"
              inputMode="numeric"
              min={MIN_BONUS_POINTS}
              max={MAX_BONUS_POINTS}
              step="1"
              className="h-10 bg-card sm:max-w-48"
              value={values[field]}
              onChange={(e) => setValues((previous) => ({ ...previous, [field]: e.target.value }))}
            />
          </Field>
        ))}
      </div>
      <p className="text-sm text-muted-foreground">{t('settings.bonuses.hint')}</p>
      {current ? (
        <p className="text-sm font-semibold">{format('settings.bonuses.since', { from: dmy(current.from) })}</p>
      ) : (
        <p className="rounded-xl border border-dashed px-4 py-3 text-sm text-muted-foreground">{t('settings.bonuses.off')}</p>
      )}
      {schedule.length > 0 && (
        <div className="flex flex-col gap-2">
          <h3 className="text-sm font-bold">{t('settings.bonuses.schedule')}</h3>
          <ul className="flex flex-col gap-2">
            {[...schedule].reverse().map((row) => (
              <li key={row.from} className={`${listRowClass} text-sm tabular-nums`}>
                {format('settings.bonuses.row', {
                  from: dmy(row.from),
                  weekDone: row.weekDone,
                  weekOnTime: row.weekOnTime,
                  cycleDone: row.cycleDone,
                  cycleOnTime: row.cycleOnTime,
                })}
                {row.from > todayKey && (
                  <span className="ml-auto inline-flex items-center gap-1 font-semibold text-muted-foreground">
                    <Clock className="size-4" aria-hidden="true" />
                    {t('settings.bonuses.future')}
                  </span>
                )}
              </li>
            ))}
          </ul>
        </div>
      )}
      {message && <FormMessage kind={message.kind}>{message.text}</FormMessage>}
      <FormActions>
        <Button type="submit" disabled={save.isPending}>
          <Save aria-hidden="true" />
          {t('settings.bonuses.save')}
        </Button>
      </FormActions>
    </form>
  );
}

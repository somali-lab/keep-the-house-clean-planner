import { Clock, Gift, Save } from 'lucide-react';
import { useId, useState, type FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import type { BonusAmounts, Settings } from '../../api/v2/household.ts';
import { FALLBACK_LIMITS, useLimits } from '../../api/v2/queries.ts';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { saveErrorText, useUpdateSettings } from './api.ts';
import { Field, FormActions, FormMessage, listRowClass, SettingsCardHeader, settingsCardClass } from './SettingsCard.tsx';

type Message = { kind: 'status' | 'alert'; text: string } | null;

const FIELDS = ['weekDone', 'weekOnTime', 'cycleDone', 'cycleOnTime'] as const satisfies readonly (keyof BonusAmounts)[];

/** The amount typed in a field, or null when it is not a whole number within the limits of the server. The server validates too. */
export function parseBonusAmount(text: string, limits: { minPoints: number; maxPoints: number } = FALLBACK_LIMITS.bonuses): number | null {
  if (!/^\d{1,6}$/.test(text.trim())) return null;
  const value = Number(text);
  return value >= limits.minPoints && value <= limits.maxPoints ? value : null;
}

const dmy = (key: string) => key.split('-').reverse().join('-');

/**
 * The amounts of the week and cycle bonuses (ADR-0012), for administrators. Saving writes a schedule row that applies from today:
 * periods that have ended keep the amounts they had, and an amount of 0 turns a bonus off. The server works out which amounts are
 * in force today (`bonusesInForce`) and which rows have not started yet (`startsInFuture`); this section only shows them.
 */
export function BonusSection({ settings }: { settings: Settings }) {
  const idPrefix = useId();
  const { profile } = useProfile();
  const limits = useLimits().data?.bonuses ?? FALLBACK_LIMITS.bonuses;
  const update = useUpdateSettings();
  const inForce = settings.bonusesInForce;
  const [values, setValues] = useState<Record<keyof BonusAmounts, string>>({
    weekDone: String(inForce.weekDone),
    weekOnTime: String(inForce.weekOnTime),
    cycleDone: String(inForce.cycleDone),
    cycleOnTime: String(inForce.cycleOnTime),
  });
  const [message, setMessage] = useState<Message>(null);

  if (profile?.role !== 'admin') return null;

  const schedule = [...settings.bonusSchedule].sort((a, b) => a.from.localeCompare(b.from));
  // The row in force is the last one that has started.
  const current = schedule.filter((row) => !row.startsInFuture).at(-1);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const parsed = FIELDS.map((field) => [field, parseBonusAmount(values[field], limits)] as const);
    if (parsed.some(([, amount]) => amount === null)) {
      setMessage({ kind: 'alert', text: format('settings.bonuses.invalid', limits) });
      return;
    }
    setMessage(null);
    update.mutate(
      { settings, patch: { periodBonuses: Object.fromEntries(parsed) as unknown as BonusAmounts } },
      {
        onSuccess: () => setMessage({ kind: 'status', text: t('settings.saved') }),
        onError: (error) =>
          setMessage({ kind: 'alert', text: saveErrorText(error, { bonus_schedule_conflict: 'settings.bonuses.conflict' }) }),
      },
    );
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
              min={limits.minPoints}
              max={limits.maxPoints}
              step="1"
              className="h-10 bg-card sm:max-w-48"
              value={values[field]}
              onChange={(e) => setValues((previous) => ({ ...previous, [field]: e.target.value }))}
            />
          </Field>
        ))}
      </div>
      <p className="text-sm text-muted-foreground">{format('settings.bonuses.hint', limits)}</p>
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
                {row.startsInFuture && (
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
        <Button type="submit" disabled={update.isPending}>
          <Save aria-hidden="true" />
          {t('settings.bonuses.save')}
        </Button>
      </FormActions>
    </form>
  );
}

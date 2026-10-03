import type { Settings } from '@huishoudplanner/shared';
import {
  DEFAULT_CURRENCY_CODE,
  formatCents,
  isTwoDecimalCurrency,
  MAX_CENTS_PER_POINT,
  MIN_CENTS_PER_POINT,
} from '@huishoudplanner/shared/points';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Coins, Save } from 'lucide-react';
import { useId, useMemo, useState, type FormEvent } from 'react';
import { NativeSelect } from '@/components/NativeSelect';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { api } from '../../api/index.ts';
import { queryKeys } from '../../api/queries.ts';
import { format, t } from '../../i18n/nl.ts';
import { getLocale } from '../../i18n/runtime.ts';
import { useProfile } from '../../identity/index.ts';
import { Field, FormActions, FormMessage, SettingsCardHeader, settingsCardClass } from './SettingsCard.tsx';

type Message = { kind: 'status' | 'alert'; text: string } | null;

/** A fallback for runtimes without `Intl.supportedValuesOf`. */
const COMMON_CURRENCIES = ['EUR', 'USD', 'GBP', 'CHF', 'SEK', 'NOK', 'DKK', 'PLN', 'CZK', 'JPY'];

/**
 * The currencies the runtime knows that have exactly two fraction digits (money is whole cents), with the
 * household's current one always among them so the select can show it.
 */
export function currencyCodes(current: string): string[] {
  const known = ((Intl as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf?.('currency') ?? COMMON_CURRENCIES).filter(
    isTwoDecimalCurrency,
  );
  return known.includes(current) ? known : [...known, current].sort();
}

/** The cents typed in the field, or null when it is not a whole number from 0 to 10000. The server validates too. */
export function parseCentsPerPoint(text: string): number | null {
  if (!/^\d{1,5}$/.test(text.trim())) return null;
  const value = Number(text);
  return value >= MIN_CENTS_PER_POINT && value <= MAX_CENTS_PER_POINT ? value : null;
}

/** "EUR – euro" in the interface language; the code alone when the runtime cannot name it. */
function currencyLabel(code: string): string {
  try {
    const name = new Intl.DisplayNames(getLocale(), { type: 'currency' }).of(code);
    return name && name !== code ? `${code} – ${name}` : code;
  } catch {
    return code;
  }
}

/**
 * The conversion from points to money (requirements 4.12), for administrators: a currency and the cents one point is
 * worth. With 0 no money is shown anywhere. A redemption keeps the value at the moment it was booked, so
 * changing this never rewrites an earlier payout.
 */
export function ConversionSection({ settings }: { settings: Settings }) {
  const idPrefix = useId();
  const queryClient = useQueryClient();
  const { profile } = useProfile();
  const currentCurrency = settings.currencyCode ?? DEFAULT_CURRENCY_CODE;
  const currentCents = settings.centsPerPoint ?? 0;
  const [currencyCode, setCurrencyCode] = useState(currentCurrency);
  const [cents, setCents] = useState(String(currentCents));
  const [message, setMessage] = useState<Message>(null);
  const codes = useMemo(() => currencyCodes(currentCurrency), [currentCurrency]);

  const save = useMutation({
    mutationFn: (body: { currencyCode: string; centsPerPoint: number }) => api.patch('/api/settings', body),
    onSuccess: async () => {
      setMessage({ kind: 'status', text: t('settings.saved') });
      // Balances and entries are shown in this currency, so they are read again too.
      await Promise.all([queryClient.invalidateQueries({ queryKey: queryKeys.settings }), queryClient.invalidateQueries({ queryKey: ['points'] })]);
    },
    onError: () => setMessage({ kind: 'alert', text: t('app.error') }),
  });

  if (profile?.role !== 'admin') return null;

  const parsed = parseCentsPerPoint(cents);
  const preview = parsed !== null && parsed > 0 ? formatCents(parsed, currencyCode, getLocale()) : null;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!isTwoDecimalCurrency(currencyCode)) {
      setMessage({ kind: 'alert', text: t('settings.conversion.currencyInvalid') });
      return;
    }
    if (parsed === null) {
      setMessage({ kind: 'alert', text: t('settings.conversion.invalid') });
      return;
    }
    setMessage(null);
    save.mutate({ currencyCode, centsPerPoint: parsed });
  };

  return (
    <form className={settingsCardClass} onSubmit={submit} noValidate aria-labelledby={`${idPrefix}-title`}>
      <SettingsCardHeader
        icon={<Coins aria-hidden="true" />}
        titleId={`${idPrefix}-title`}
        title={t('settings.conversion.title')}
        description={t('settings.conversion.explainer')}
      />
      <div className="grid gap-4 sm:grid-cols-2">
        <Field>
          <Label htmlFor={`${idPrefix}-currency`}>{t('settings.conversion.currency')}</Label>
          <NativeSelect
            id={`${idPrefix}-currency`}
            className="[&_select]:h-10 sm:max-w-72"
            value={currencyCode}
            onChange={(event) => setCurrencyCode(event.target.value)}
          >
            {codes.map((code) => (
              <option key={code} value={code}>
                {currencyLabel(code)}
              </option>
            ))}
          </NativeSelect>
        </Field>
        <Field>
          <Label htmlFor={`${idPrefix}-cents`}>{t('settings.conversion.centsPerPoint')}</Label>
          <Input
            id={`${idPrefix}-cents`}
            type="number"
            inputMode="numeric"
            min={MIN_CENTS_PER_POINT}
            max={MAX_CENTS_PER_POINT}
            step="1"
            className="h-10 bg-card sm:max-w-48"
            value={cents}
            onChange={(event) => setCents(event.target.value)}
          />
        </Field>
      </div>
      <p className="text-sm text-muted-foreground">{t('settings.conversion.hint')}</p>
      {preview ? (
        <p className="text-sm font-semibold">{format('settings.conversion.preview', { money: preview })}</p>
      ) : (
        <p className="rounded-xl border border-dashed px-4 py-3 text-sm text-muted-foreground">{t('settings.conversion.off')}</p>
      )}
      {message && <FormMessage kind={message.kind}>{message.text}</FormMessage>}
      <FormActions>
        <Button type="submit" disabled={save.isPending}>
          <Save aria-hidden="true" />
          {t('settings.conversion.save')}
        </Button>
      </FormActions>
    </form>
  );
}

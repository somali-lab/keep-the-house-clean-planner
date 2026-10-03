import { formatCents } from '@huishoudplanner/shared/points';
import type { PointEntryView } from '@huishoudplanner/shared';
import { HandCoins, TriangleAlert } from 'lucide-react';
import { useId, useRef, useState, type FormEvent } from 'react';
import { NativeSelect } from '@/components/NativeSelect';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { ApiRequestError } from '../../api/index.ts';
import { format, t } from '../../i18n/nl.ts';
import { getLocale } from '../../i18n/runtime.ts';
import { useProfile } from '../../identity/index.ts';
import { useAllTimeBalances, useRedeemPoints } from './api.ts';
import { buildRedemption, redemptionCents, type RedeemField, type RedeemForm } from './pointsModel.ts';
import { formatNumber } from './scale.ts';

interface RedeemDialogProps {
  open: boolean;
  onOpenChange(open: boolean): void;
  /** Called after the booking succeeded, with the stored entry and the money it is worth (null while a point is worth nothing). */
  onRedeemed?(entry: PointEntryView, money: string | null, replayed: boolean): void;
}

/**
 * Redeems points (requirements 4.12): a person gives up points for a payout or a reward. Everybody redeems for the
 * active profile; an administrator can pick anyone. The dialog shows what the points are worth when the
 * household set a conversion, and cannot book more than the balance (the server checks that too).
 */
export function RedeemDialog({ open, onOpenChange, onRedeemed }: RedeemDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[calc(100dvh-2rem)] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>{t('redeem.title')}</DialogTitle>
          <DialogDescription>{t('redeem.description')}</DialogDescription>
        </DialogHeader>
        {/* Mounted only while open, so every opening starts with an empty form and a fresh balance. */}
        <RedeemFormBody
          onCancel={() => onOpenChange(false)}
          onRedeemed={(entry, money, replayed) => {
            onRedeemed?.(entry, money, replayed);
            onOpenChange(false);
          }}
        />
      </DialogContent>
    </Dialog>
  );
}

const FIELD_ORDER: RedeemField[] = ['points', 'note'];

function RedeemFormBody({
  onCancel,
  onRedeemed,
}: {
  onCancel(): void;
  onRedeemed(entry: PointEntryView, money: string | null, replayed: boolean): void;
}) {
  const idPrefix = useId();
  const { profile, activeUsers } = useProfile();
  const balances = useAllTimeBalances();
  const redeem = useRedeemPoints();
  const formRef = useRef<HTMLFormElement>(null);
  const isAdmin = profile?.role === 'admin';
  const [chosenPerson, setChosenPerson] = useState('');
  const [form, setForm] = useState<RedeemForm>({ points: '', note: '' });
  const [submitted, setSubmitted] = useState(false);
  const [failure, setFailure] = useState<'insufficient' | 'failed' | null>(null);
  // A second click can arrive before the pending state has rendered, so the guard is synchronous.
  const inFlight = useRef(false);

  const personId = isAdmin && chosenPerson ? chosenPerson : (profile?._id ?? '');
  const row = balances.data?.balances.find((balance) => balance.personId === personId);
  const balance = row?.points ?? 0;
  const centsPerPoint = balances.data?.centsPerPoint ?? 0;
  const currencyCode = balances.data?.currencyCode ?? 'EUR';
  const money = (cents: number) => formatCents(cents, currencyCode, getLocale());
  const preview = redemptionCents(form.points, centsPerPoint);

  const result = buildRedemption(form, balance);
  const errors = submitted && !result.ok ? result.errors : {};
  const set = <K extends keyof RedeemForm>(key: K, value: RedeemForm[K]) => {
    setFailure(null);
    setForm((current) => ({ ...current, [key]: value }));
  };
  const fieldProps = (field: RedeemField) => ({
    'aria-invalid': errors[field] ? true : undefined,
    'aria-describedby': errors[field] ? `${idPrefix}-${field}-error` : undefined,
  });
  const fieldError = (field: RedeemField) => {
    const key = errors[field];
    return key ? (
      <p id={`${idPrefix}-${field}-error`} className="flex items-center gap-1.5 text-sm font-semibold text-destructive">
        <TriangleAlert className="size-4 shrink-0" aria-hidden="true" />
        {format(key, { points: formatNumber(balance) })}
      </p>
    ) : null;
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (inFlight.current) return;
    setSubmitted(true);
    if (!result.ok) {
      // Move focus to the first field that needs attention.
      const first = FIELD_ORDER.find((field) => result.errors[field]);
      if (first) formRef.current?.querySelector<HTMLElement>(`[id="${idPrefix}-${first}"]`)?.focus();
      return;
    }
    inFlight.current = true;
    setFailure(null);
    try {
      const { entry, replayed } = await redeem.mutateAsync({ personId, points: result.points, note: result.note });
      // The booking is shown in the currency and at the factor it was stored with.
      const worth = entry.centsPerPointSnapshot
        ? formatCents(-entry.amount * entry.centsPerPointSnapshot, entry.currencyCodeSnapshot ?? currencyCode, getLocale())
        : null;
      onRedeemed(entry, worth, replayed);
    } catch (error) {
      setFailure(error instanceof ApiRequestError && error.code === 'insufficient_balance' ? 'insufficient' : 'failed');
    } finally {
      inFlight.current = false;
    }
  };

  const loading = balances.isPending;
  const noBalance = !loading && balance < 1;
  const errorFields = FIELD_ORDER.filter((field) => errors[field]);

  return (
    <form ref={formRef} className="grid gap-5" onSubmit={submit} noValidate aria-label={t('redeem.title')}>
      {isAdmin && (
        <div className="grid gap-1.5">
          <Label htmlFor={`${idPrefix}-person`}>{t('redeem.person')}</Label>
          <NativeSelect
            id={`${idPrefix}-person`}
            className="[&_select]:h-11"
            value={personId}
            onChange={(event) => {
              setFailure(null);
              setChosenPerson(event.target.value);
            }}
          >
            {activeUsers.map((user) => (
              <option key={user._id} value={user._id}>
                {user.name}
              </option>
            ))}
          </NativeSelect>
        </div>
      )}

      <p role="status" className="flex items-center gap-2 rounded-xl bg-secondary/60 px-3 py-2 text-sm font-semibold">
        <HandCoins className="size-4 shrink-0 text-primary" aria-hidden="true" />
        {loading
          ? t('app.loading')
          : centsPerPoint > 0
            ? format('redeem.availableMoney', { points: formatNumber(balance), money: money(balance * centsPerPoint) })
            : format('redeem.available', { points: formatNumber(balance) })}
      </p>
      {noBalance && <p className="text-sm text-muted-foreground">{t('redeem.noBalance')}</p>}

      <div className="grid gap-1.5">
        <Label htmlFor={`${idPrefix}-points`}>{t('redeem.points')}</Label>
        <Input
          id={`${idPrefix}-points`}
          className="h-11"
          type="number"
          inputMode="numeric"
          min={1}
          step={1}
          value={form.points}
          onChange={(event) => set('points', event.target.value)}
          {...fieldProps('points')}
        />
        {fieldError('points')}
        {preview !== null && (
          <p role="status" className="text-sm font-semibold text-muted-foreground">
            {format('redeem.preview', { money: money(preview) })}
          </p>
        )}
      </div>

      <div className="grid gap-1.5">
        <Label htmlFor={`${idPrefix}-note`}>{t('redeem.note')}</Label>
        <Input
          id={`${idPrefix}-note`}
          className="h-11"
          value={form.note}
          autoComplete="off"
          onChange={(event) => set('note', event.target.value)}
          {...fieldProps('note')}
        />
        <p className="text-sm text-muted-foreground">{t('redeem.noteHint')}</p>
        {fieldError('note')}
      </div>

      {errorFields.length > 0 && (
        <p role="alert" className="flex items-center gap-2 rounded-xl bg-destructive/10 p-3 text-sm font-semibold text-destructive">
          <TriangleAlert className="size-4 shrink-0" aria-hidden="true" />
          {t('redeem.error.summary')}
        </p>
      )}
      {failure && (
        <p role="alert" className="flex items-center gap-2 rounded-xl bg-destructive/10 p-3 text-sm font-semibold text-destructive">
          <TriangleAlert className="size-4 shrink-0" aria-hidden="true" />
          {failure === 'insufficient' ? t('redeem.error.insufficient') : t('redeem.error.failed')}
        </p>
      )}

      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <Button type="button" variant="outline" className="h-11 rounded-full" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button type="submit" className="h-11 rounded-full" disabled={redeem.isPending || !profile || loading || noBalance}>
          {redeem.isPending ? t('redeem.submitting') : t('redeem.submit')}
        </Button>
      </div>
    </form>
  );
}

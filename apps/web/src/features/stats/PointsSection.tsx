import { formatCents } from '@huishoudplanner/shared/points';
import { Award, Gift, HandCoins, Undo2 } from 'lucide-react';
import { useId, useState } from 'react';
import { NativeSelect } from '@/components/NativeSelect';
import { Button } from '@/components/ui/button';
import { Label } from '@/components/ui/label';
import { ApiRequestError } from '../../api/index.ts';
import { useSettings, useUsers } from '../../api/v2/household.ts';
import { format, t } from '../../i18n/nl.ts';
import { getLocale } from '../../i18n/runtime.ts';
import { useProfile } from '../../identity/index.ts';
import { PersonBadges } from '../badges/PersonBadges.tsx';
import { dayKeyInZone } from '@/lib/dayKey';
import { useAllTimeBalances, usePointsBalances, usePointsEntries, useUndoRedemption, type StatsPeriod } from './api.ts';
import { statsTableClass } from './ChartFrame.tsx';
import { bonusText } from './bonusText.ts';
import { bonusLabel, canUndoRedemption, pointsRange } from './pointsModel.ts';
import { RedeemDialog } from './RedeemDialog.tsx';
import { formatNumber } from './scale.ts';

const sectionCardClass = 'flex flex-col gap-5 rounded-2xl border bg-card p-6 text-card-foreground shadow-sm';
const noneClass = 'rounded-xl border border-dashed px-4 py-3 text-sm text-muted-foreground';

/** "16 september 2026" in the interface language; a day key is a calendar date, so UTC keeps it unchanged. */
function longDate(dayKey: string): string {
  return new Intl.DateTimeFormat(getLocale(), { dateStyle: 'long', timeZone: 'UTC' }).format(new Date(`${dayKey}T00:00:00Z`));
}

/**
 * The Points tab (ADR-0011, ADR-0012): the balance of every person and the entries of one person for the
 * period chosen with the statistics period control.
 */
export function PointsSection({ period, now }: { period: StatsPeriod; now?: Date }) {
  const idPrefix = useId();
  const { profile } = useProfile();
  const users = useUsers();
  const settings = useSettings();
  const [chosen, setChosen] = useState<string | null>(null);
  const [redeemOpen, setRedeemOpen] = useState(false);
  const [message, setMessage] = useState<{ kind: 'status' | 'alert'; text: string } | null>(null);
  const undo = useUndoRedemption();
  const allTime = useAllTimeBalances();

  const range = settings.data
    ? pointsRange(period, settings.data.cycleAnchorDate, dayKeyInZone(now ?? new Date(), settings.data.timezone))
    : null;
  const balances = usePointsBalances(range);

  const nameOf = (id: string) => users.data?.find((user) => user.id === id)?.name ?? t('tasks.unknownUser');
  const isInactive = (id: string) => users.data?.find((user) => user.id === id)?.active === false;
  const label = (id: string) => (isInactive(id) ? format('stats.points.inactive', { name: nameOf(id) }) : nameOf(id));

  const rows = balances.data?.balances ?? [];
  const personId =
    chosen && rows.some((row) => row.personId === chosen)
      ? chosen
      : ((rows.find((row) => row.personId === profile?.id) ?? rows[0])?.personId ?? null);
  const entries = usePointsEntries(personId, range);
  const todayKey = settings.data ? dayKeyInZone(now ?? new Date(), settings.data.timezone) : '';

  if (settings.isError || balances.isError) {
    return (
      <p role="alert" className="rounded-xl bg-destructive/10 p-4 text-destructive">
        {t('stats.points.error')}
      </p>
    );
  }
  if (!range || balances.isPending) {
    return (
      <p role="status" className="text-muted-foreground">
        {t('app.loading')}
      </p>
    );
  }

  const earned = rows.some((row) => row.executions > 0 || row.points !== 0 || row.redeemed > 0);
  const centsPerPoint = balances.data?.centsPerPoint ?? 0;
  const currencyCode = balances.data?.currencyCode ?? 'EUR';
  const money = (cents: number, currency: string = currencyCode) => formatCents(cents, currency, getLocale());
  /** The balance over the whole ledger, the number the redeem dialog works with; a dash until it is known. */
  const allTimeOf = (id: string) => allTime.data?.balances.find((balance) => balance.personId === id);
  const hasRedemptions = entries.data?.entries.some((entry) => entry.kind === 'redemption') ?? false;

  const undoRedemption = (id: string) => {
    setMessage(null);
    undo.mutate(id, {
      onSuccess: () => setMessage({ kind: 'status', text: t('stats.points.undone') }),
      onError: (error) =>
        setMessage({
          kind: 'alert',
          text:
            error instanceof ApiRequestError && error.code === 'redemption_locked'
              ? t('stats.points.undoLocked')
              : t('stats.points.undoError'),
        }),
    });
  };

  return (
    <section className={sectionCardClass} aria-labelledby={`${idPrefix}-points`}>
      <div className="flex items-start gap-3">
        <div className="grid size-10 shrink-0 place-items-center rounded-xl bg-accent text-accent-foreground [&_svg]:size-5">
          <Award aria-hidden="true" />
        </div>
        <div className="min-w-0">
          <h2 id={`${idPrefix}-points`} className="text-lg leading-tight font-bold">
            {t('stats.points')}
          </h2>
          <p className="mt-1 text-sm text-muted-foreground">
            {format('stats.points.explainer', { from: longDate(range.from), to: longDate(range.to) })}
          </p>
        </div>
        {profile && (
          <Button type="button" className="ml-auto h-10 shrink-0 rounded-full" onClick={() => setRedeemOpen(true)}>
            <HandCoins aria-hidden="true" />
            {t('stats.points.redeem')}
          </Button>
        )}
      </div>
      {message && (
        <p
          role={message.kind}
          className={`rounded-xl px-3 py-2 text-sm font-semibold ${message.kind === 'status' ? 'bg-success/15 text-success' : 'bg-destructive/10 text-destructive'}`}
        >
          {message.text}
        </p>
      )}
      <RedeemDialog
        open={redeemOpen}
        onOpenChange={setRedeemOpen}
        onRedeemed={(entry, worth, replayed) =>
          setMessage({
            kind: 'status',
            text: replayed
              ? t('stats.points.redeemedReplay')
              : worth
                ? format('stats.points.redeemedDoneMoney', { amount: formatNumber(-entry.amount), money: worth })
                : format('stats.points.redeemedDone', { amount: formatNumber(-entry.amount) }),
          })
        }
      />

      {earned ? (
        <div className="overflow-x-auto">
          <table className={statsTableClass}>
            <caption>{t('stats.points.balances')}</caption>
            <thead>
              <tr>
                <th scope="col">{t('stats.person')}</th>
                <th scope="col">{t('stats.points.balance')}</th>
                <th scope="col">{t('stats.points.executions')}</th>
                <th scope="col">{t('stats.points.bonus')}</th>
                <th scope="col">{t('stats.points.redeemed')}</th>
                {centsPerPoint > 0 && <th scope="col">{t('stats.points.worth')}</th>}
                <th scope="col">{t('stats.points.allTime')}</th>
                {centsPerPoint > 0 && <th scope="col">{t('stats.points.allTimeWorth')}</th>}
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.personId}>
                  <th scope="row">{label(row.personId)}</th>
                  <td className="font-bold text-primary tabular-nums">{formatNumber(row.points)}</td>
                  <td className="tabular-nums">{formatNumber(row.executions)}</td>
                  <td className="tabular-nums">{formatNumber(row.bonusPoints)}</td>
                  <td className="tabular-nums">{formatNumber(row.redeemed)}</td>
                  {centsPerPoint > 0 && <td className="tabular-nums">{money(row.money?.balance ?? row.points * centsPerPoint)}</td>}
                  <td className="font-bold tabular-nums">{allTime.data ? formatNumber(allTimeOf(row.personId)?.points ?? 0) : '—'}</td>
                  {centsPerPoint > 0 && (
                    <td className="tabular-nums">
                      {allTime.data ? money(allTimeOf(row.personId)?.money?.balance ?? 0, allTime.data.currencyCode) : '—'}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <p className={noneClass}>{t('stats.points.noBalances')}</p>
      )}

      {personId && (
        <div className="flex flex-col gap-4">
          <div className="flex w-full max-w-xs flex-col gap-2">
            <Label htmlFor={`${idPrefix}-person`}>{t('stats.points.person')}</Label>
            <NativeSelect id={`${idPrefix}-person`} value={personId} onChange={(e) => setChosen(e.target.value)}>
              {rows.map((row) => (
                <option key={row.personId} value={row.personId}>
                  {label(row.personId)}
                </option>
              ))}
            </NativeSelect>
          </div>
          {entries.isError ? (
            <p role="alert" className="rounded-xl bg-destructive/10 p-4 text-destructive">
              {t('stats.points.error')}
            </p>
          ) : entries.data && !entries.isPlaceholderData && entries.data.entries.length > 0 ? (
            <div className="overflow-x-auto">
              <table className={statsTableClass}>
                <caption>{format('stats.points.entries', { name: nameOf(personId) })}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t('stats.points.date')}</th>
                    <th scope="col">{t('stats.points.task')}</th>
                    <th scope="col">{t('stats.points.amount')}</th>
                    {hasRedemptions && <th scope="col">{t('stats.points.actions')}</th>}
                  </tr>
                </thead>
                <tbody>
                  {entries.data.entries.map((entry) => {
                    const bonus = bonusLabel(entry);
                    const redemption = entry.kind === 'redemption';
                    return (
                    <tr key={entry._id}>
                      <th scope="row" className="whitespace-nowrap">
                        {longDate(entry.date)}
                      </th>
                      <td>
                        {redemption ? (
                          <span className="inline-flex items-center gap-2 font-semibold">
                            <HandCoins className="size-4 shrink-0 text-primary" aria-hidden="true" />
                            {entry.note ? format('stats.points.redemptionNote', { note: entry.note }) : t('stats.points.redemption')}
                          </span>
                        ) : bonus ? (
                          <span className="inline-flex items-center gap-2 font-semibold">
                            <Gift className="size-4 shrink-0 text-primary" aria-hidden="true" />
                            {bonusText(bonus)}
                          </span>
                        ) : (
                          entry.titleSnapshot
                        )}
                      </td>
                      <td className="font-bold tabular-nums">
                        {entry.amount > 0 ? `+${entry.amount}` : entry.amount}
                        {redemption && (entry.centsPerPointSnapshot ?? 0) > 0 && (
                          <span className="font-normal text-muted-foreground">
                            {' '}
                            ({money(-entry.amount * (entry.centsPerPointSnapshot ?? 0), entry.currencyCodeSnapshot ?? currencyCode)})
                          </span>
                        )}
                      </td>
                      {hasRedemptions && (
                        <td>
                          {canUndoRedemption(entry, profile, todayKey) && (
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              className="rounded-full"
                              disabled={undo.isPending}
                              aria-label={format('stats.points.undoLabel', { amount: -entry.amount })}
                              onClick={() => undoRedemption(entry._id)}
                            >
                              <Undo2 aria-hidden="true" />
                              {t('stats.points.undo')}
                            </Button>
                          )}
                        </td>
                      )}
                    </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          ) : (
            <p className={noneClass}>
              {entries.isPending || entries.isPlaceholderData ? t('app.loading') : format('stats.points.noEntries', { name: nameOf(personId) })}
            </p>
          )}
          <PersonBadges personId={personId} title={format('badges.of', { name: nameOf(personId) })} />
        </div>
      )}
    </section>
  );
}

import { Award, Gift } from 'lucide-react';
import { useId, useState } from 'react';
import { NativeSelect } from '@/components/NativeSelect';
import { Label } from '@/components/ui/label';
import { useSettings, useUsers } from '../../api/queries.ts';
import { format, t } from '../../i18n/nl.ts';
import { getLocale } from '../../i18n/runtime.ts';
import { useProfile } from '../../identity/index.ts';
import { dayKeyInZone } from '../today/todayModel.ts';
import { usePointsBalances, usePointsEntries, type StatsPeriod } from './api.ts';
import { statsTableClass } from './ChartFrame.tsx';
import { bonusLabel, pointsRange } from './pointsModel.ts';
import { formatNumber } from './scale.ts';

const sectionCardClass = 'flex flex-col gap-5 rounded-2xl border bg-card p-6 text-card-foreground shadow-sm';
const noneClass = 'rounded-xl border border-dashed px-4 py-3 text-sm text-muted-foreground';

/** "16 september 2026" in the interface language; a day key is a calendar date, so UTC keeps it unchanged. */
function longDate(dayKey: string): string {
  return new Intl.DateTimeFormat(getLocale(), { dateStyle: 'long', timeZone: 'UTC' }).format(new Date(`${dayKey}T00:00:00Z`));
}

/** "7 sep" in the interface language: the short form that labels the days of a cycle bonus. */
function shortDate(dayKey: string): string {
  return new Intl.DateTimeFormat(getLocale(), { day: 'numeric', month: 'short', timeZone: 'UTC' }).format(new Date(`${dayKey}T00:00:00Z`));
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

  const range = settings.data
    ? pointsRange(period, settings.data.cycleAnchorDate, dayKeyInZone(now ?? new Date(), settings.data.timezone))
    : null;
  const balances = usePointsBalances(range);

  const nameOf = (id: string) => users.data?.find((user) => user._id === id)?.name ?? t('tasks.unknownUser');
  const isInactive = (id: string) => users.data?.find((user) => user._id === id)?.active === false;
  const label = (id: string) => (isInactive(id) ? format('stats.points.inactive', { name: nameOf(id) }) : nameOf(id));

  const rows = balances.data?.balances ?? [];
  const personId =
    chosen && rows.some((row) => row.personId === chosen)
      ? chosen
      : ((rows.find((row) => row.personId === profile?._id) ?? rows[0])?.personId ?? null);
  const entries = usePointsEntries(personId, range);

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

  const earned = rows.some((row) => row.executions > 0 || row.points !== 0);

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
      </div>

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
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.personId}>
                  <th scope="row">{label(row.personId)}</th>
                  <td className="font-bold text-primary tabular-nums">{formatNumber(row.points)}</td>
                  <td className="tabular-nums">{formatNumber(row.executions)}</td>
                  <td className="tabular-nums">{formatNumber(row.bonusPoints)}</td>
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
                  </tr>
                </thead>
                <tbody>
                  {entries.data.entries.map((entry) => {
                    const bonus = bonusLabel(entry);
                    return (
                    <tr key={entry._id}>
                      <th scope="row" className="whitespace-nowrap">
                        {longDate(entry.date)}
                      </th>
                      <td>
                        {bonus ? (
                          <span className="inline-flex items-center gap-2 font-semibold">
                            <Gift className="size-4 shrink-0 text-primary" aria-hidden="true" />
                            {format(bonus.key, { week: bonus.week, from: shortDate(bonus.from), to: shortDate(bonus.to) })}
                          </span>
                        ) : (
                          entry.titleSnapshot
                        )}
                      </td>
                      <td className="font-bold tabular-nums">{entry.amount > 0 ? `+${entry.amount}` : entry.amount}</td>
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
        </div>
      )}
    </section>
  );
}

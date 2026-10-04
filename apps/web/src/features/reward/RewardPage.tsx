import { PartyPopper } from 'lucide-react';
import { useEffect, useId, useRef, useState } from 'react';
import { useSettings } from '../../api/v2/household.ts';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { usePersistedFilter } from '../../hooks/usePersistedFilter.ts';
import { useReducedMotion } from '../../hooks/useReducedMotion.ts';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import { getLocale } from '../../i18n/runtime.ts';
import { useProfile } from '../../identity/index.ts';
import { PersonBadges } from '../badges/PersonBadges.tsx';
import { compactDate } from '../mobile-tasks/taskOverviewModel.ts';
import { dayKeyInZone } from '@/lib/dayKey';
import { formatMoney } from '@/lib/money';
import { usePointsProgress, type RewardProgress } from './api.ts';
import { RewardMeter } from './RewardMeter.tsx';
import {
  celebrationKey,
  celebrationMode,
  goalReached,
  markCelebrated,
  periodRolledOver,
  REWARD_PERIODS,
  wasCelebrated,
  type RewardPeriod,
} from './rewardModel.ts';

/**
 * The reward tab (requirements 4.12): progress of the active profile towards the goal of this week or cycle as earned points,
 * their worth in money, eggs in a basket and a walking chicken, with the earned badges below it. The layout is
 * deliberately simple and accessible; the maintainer will fine-tune it.
 */
/** How long the completion animation may run before the page stops treating it as running, in case no end event comes. */
const CELEBRATION_MAX_MS = 5000;
/** How often an open tab checks whether the week or cycle rolled over. */
const ROLLOVER_CHECK_MS = 60_000;

export function RewardPage({ now }: { now?: Date }) {
  const { profile } = useProfile();
  const settings = useSettings();
  const timezone = settings.data?.timezone ?? 'Europe/Amsterdam';
  const [stored, setPeriod] = usePersistedFilter<RewardPeriod>('reward.period', profile?.id ?? null, 'week');
  // A stored value that is not a period (hand-edited storage) falls back to the week.
  const period: RewardPeriod = stored === 'cycle' ? 'cycle' : 'week';
  const progress = usePointsProgress(profile?.id ?? null, period);
  const data = progress.data;

  // The current day by the clock of this device. When it lies outside the period that was read (the week or cycle rolled
  // over while the tab stayed open), the progress is read again instead of showing last week's meter.
  const [todayKey, setTodayKey] = useState(() => dayKeyInZone(now ?? new Date(), timezone));
  useEffect(() => {
    setTodayKey(dayKeyInZone(now ?? new Date(), timezone));
    if (now) return;
    const id = setInterval(() => setTodayKey(dayKeyInZone(new Date(), timezone)), ROLLOVER_CHECK_MS);
    return () => clearInterval(id);
  }, [now, timezone]);
  const refetch = progress.refetch;
  const staleFor = data && periodRolledOver(data, todayKey) ? `${data.start}.${todayKey}` : null;
  useEffect(() => {
    if (staleFor !== null) void refetch();
  }, [staleFor, refetch]);

  return (
    <section className="flex flex-col gap-5">
      <PageHeader
        title={t('reward.title')}
        description={data ? format('reward.range', { from: compactDate(data.start), to: compactDate(data.end) }) : undefined}
        className="mb-0"
      />

      <fieldset className="grid gap-1.5">
        <legend className="text-sm font-semibold">{t('reward.period')}</legend>
        <div className="grid max-w-xs grid-cols-2 rounded-lg bg-muted p-1">
          {REWARD_PERIODS.map((option) => (
            <Button
              key={option}
              type="button"
              variant="ghost"
              size="sm"
              className={cn(
                'h-8 rounded-md px-3 shadow-none',
                period === option && 'bg-background text-foreground shadow-sm hover:bg-background',
              )}
              aria-pressed={period === option}
              onClick={() => setPeriod(option)}
            >
              {t(`reward.period.${option}` as MessageKey)}
            </Button>
          ))}
        </div>
      </fieldset>

      {progress.isPending ? (
        <p role="status" className="py-6 text-center text-muted-foreground">
          {t('app.loading')}
        </p>
      ) : progress.isError || !data ? (
        <p role="alert" className="rounded-2xl bg-destructive/10 p-4 text-destructive">
          {t('reward.loadError')}
        </p>
      ) : (
        <RewardCard key={`${data.personId}.${data.period}.${data.start}`} progress={data} />
      )}

      <PersonBadges personId={profile?.id ?? null} title={t('badges.my')} headingLevel={2} />
    </section>
  );
}

function RewardCard({ progress }: { progress: RewardProgress }) {
  const headingId = useId();
  const reducedMotion = useReducedMotion();
  const key = celebrationKey(progress.personId, progress.period, progress.start);
  const reached = goalReached(progress);
  const [celebratingKey, setCelebratingKey] = useState<string | null>(null);
  // Decide once per card and period, also when React runs the effect twice in development.
  const decided = useRef<string | null>(null);

  useEffect(() => {
    if (!reached || decided.current === key) return;
    decided.current = key;
    if (celebrationMode({ reached, alreadyCelebrated: wasCelebrated(key), reducedMotion }) !== 'animate') return;
    markCelebrated(key);
    setCelebratingKey(key);
  }, [key, reached, reducedMotion]);

  // The animation is over when its last egg has landed (or after a safety timeout); the meter then stays still, so a later
  // undo and redo of the last task, which fills the meter again, never replays it.
  useEffect(() => {
    if (celebratingKey === null) return;
    const id = setTimeout(() => setCelebratingKey(null), CELEBRATION_MAX_MS);
    return () => clearTimeout(id);
  }, [celebratingKey]);

  const celebrating = reached && celebratingKey === key;
  const { eggs, eggCount } = progress;
  const eggsText = format('reward.eggs', { count: eggs, total: eggCount });
  const locale = getLocale();
  const money = (cents: number) => formatMoney(cents, progress.currencyCode, locale);
  const title = t(`reward.progress.${progress.period}` as MessageKey);

  const noGoal = progress.goalPoints === null;
  const summary =
    progress.goalPoints === null
      ? ''
      : format('reward.summary', { earned: progress.earnedPoints, goal: progress.goalPoints, percent: progress.percent });
  return (
    <section
      className={cn('grid gap-3 rounded-2xl border bg-card p-4', noGoal ? 'border-dashed text-center' : 'shadow-sm')}
      aria-labelledby={headingId}
    >
      <h2 id={headingId} className="text-base font-bold">
        {title}
      </h2>
      {noGoal ? (
        <>
          <p className="font-semibold">{t('reward.noGoal')}</p>
          <p className="text-sm text-muted-foreground">
            {t(progress.goalSource === 'explicit' ? 'reward.noGoalExplicit' : 'reward.noGoalHint')}
          </p>
          <p className="text-sm">{format('reward.earnedOnly', { earned: progress.earnedPoints })}</p>
          {progress.money && <p className="text-sm">{format('reward.moneyEarned', { earned: money(progress.money.earned) })}</p>}
        </>
      ) : (
        <>
          <div className="grid place-items-center">
            <RewardMeter
              percent={progress.percent}
              eggs={eggs}
              eggCount={eggCount}
              label={format('reward.scene', { eggs: eggsText })}
              celebrating={celebrating}
              reducedMotion={reducedMotion}
              onCelebrationEnd={() => setCelebratingKey(null)}
            />
          </div>
          <div
            role="progressbar"
            aria-label={title}
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={progress.percent}
            aria-valuetext={summary}
            className="h-3 w-full overflow-hidden rounded-full border bg-muted"
          >
            <div className="h-full rounded-full bg-primary" style={{ width: `${progress.percent}%` }} />
          </div>
          <p className="text-lg font-extrabold">{summary}</p>
          {progress.money && (
            <p className="text-sm font-semibold">
              {progress.money.goal === null
                ? format('reward.moneyEarned', { earned: money(progress.money.earned) })
                : format('reward.money', { earned: money(progress.money.earned), goal: money(progress.money.goal) })}
            </p>
          )}
          <p className="text-sm">{eggsText}</p>
          <p className="text-xs text-muted-foreground">
            {t(progress.goalSource === 'explicit' ? 'reward.goalSource.explicit' : 'reward.goalSource.automatic')}
          </p>
        </>
      )}
      {/* The live region is always in the page and only its text appears, so assistive technology announces the change. */}
      <div role="status" className={cn(reached && 'flex items-center gap-2 rounded-xl bg-success/15 px-3 py-2 font-bold text-foreground')}>
        {reached && (
          <>
            <PartyPopper className="size-5 shrink-0 text-success" aria-hidden="true" />
            {t('reward.goalReached')}
          </>
        )}
      </div>
    </section>
  );
}

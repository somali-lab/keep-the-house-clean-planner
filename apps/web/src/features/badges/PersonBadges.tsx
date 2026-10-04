import { CircleCheck } from 'lucide-react';
import { useId } from 'react';
import { cn } from '@/lib/utils';
import { useSettings } from '../../api/v2/household.ts';
import { format, t } from '../../i18n/nl.ts';
import { getLocale } from '../../i18n/runtime.ts';
import { useBadgeProgress, useBadges } from './api.ts';
import { BadgeImage } from './BadgeImage.tsx';
import { badgesWithProgress, progressText } from './badgeModel.ts';

/** "16 september 2026" in the household timezone and the interface language. */
function earnedDate(instant: string, timezone: string): string {
  return new Intl.DateTimeFormat(getLocale(), { dateStyle: 'long', timeZone: timezone }).format(new Date(instant));
}

/**
 * The badges of one person (ADR-0014): the earned ones with the day they were earned, and the others with how far the
 * person is ("7/10"). Every state is also text, never only a dimmed picture. It renders nothing while there is nothing
 * to show, and nothing when the badges cannot be read: it is an extra on a page that works without it.
 */
export function PersonBadges({
  personId,
  title,
  headingLevel = 3,
  className,
}: {
  personId: string | null;
  title: string;
  headingLevel?: 2 | 3;
  className?: string;
}) {
  const headingId = useId();
  const badges = useBadges();
  const progress = useBadgeProgress(personId);
  const settings = useSettings();
  if (!badges.data || !progress.data || badges.data.every((badge) => !badge.active)) return null;

  const timezone = settings.data?.timezone ?? 'Europe/Amsterdam';
  const views = badgesWithProgress(badges.data, progress.data.items);
  const earned = views.filter((view) => view.awardedAt !== null).length;
  const Heading = headingLevel === 2 ? 'h2' : 'h3';

  return (
    <section className={cn('flex flex-col gap-3', className)} aria-labelledby={headingId}>
      <div className="flex flex-wrap items-baseline gap-2">
        <Heading id={headingId} className="text-base font-bold">
          {title}
        </Heading>
        <span className="text-sm text-muted-foreground">{format('badges.earnedCount', { count: earned })}</span>
      </div>
      <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
        {views.map(({ badge, current, threshold, awardedAt }) => (
          <li key={badge.id} className="flex items-center gap-3 rounded-xl border bg-background/60 p-3">
            <BadgeImage badge={badge} muted={awardedAt === null} />
            <div className="min-w-0">
              <p className="font-bold [overflow-wrap:anywhere]">{badge.name}</p>
              {badge.description && <p className="text-sm text-muted-foreground [overflow-wrap:anywhere]">{badge.description}</p>}
              {awardedAt ? (
                <p className="mt-1 flex items-center gap-1.5 text-sm font-semibold text-success">
                  <CircleCheck className="size-4 shrink-0" aria-hidden="true" />
                  {format('badges.earnedOn', { date: earnedDate(awardedAt, timezone) })}
                </p>
              ) : (
                <p className="mt-1 text-sm text-muted-foreground">
                  {t('badges.notEarned')}
                  {' · '}
                  <span aria-hidden="true">{progressText(current, threshold)}</span>
                  <span className="visually-hidden">{format('badges.progress', { current: Math.min(current, threshold), threshold })}</span>
                </p>
              )}
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}

/** A small "Mijn badges" section for the active profile. */
export function MyBadges({ personId }: { personId: string | null }) {
  return <PersonBadges personId={personId} title={t('badges.my')} headingLevel={2} />;
}

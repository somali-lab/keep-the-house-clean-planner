import type { OccurrenceView } from '@huishoudplanner/shared';
import { addDays, fromDayKeyTime, toDayKey } from '@huishoudplanner/shared/time';
import { format, t } from '../../i18n/nl.ts';
import { getLocale } from '../../i18n/runtime.ts';

/** A moment is still delivered this long after it, when the tab was not open at the exact time. */
export const GRACE_MS = 10 * 60_000;

/** Longest wait between two checks, so a sleeping computer or a changed clock is noticed within a minute. */
export const MAX_CHECK_INTERVAL_MS = 60_000;

/** Task names listed in the body before the rest is summarised as "+N meer". */
export const MAX_LISTED_TASKS = 5;

/** One configured notification time on one calendar day of the household timezone. */
export interface NotificationMoment {
  dayKey: string;
  /** 'HH:mm' as configured. */
  time: string;
  /** The instant that time occurs on that day. */
  at: Date;
}

export interface NotificationPlan {
  /** Moments that are due now: reached, and not more than the grace period ago. Oldest first. */
  deliver: NotificationMoment[];
  /** The first moment still to come, or null without configured times. */
  next: NotificationMoment | null;
}

function momentsOnDay(dayKey: string, times: readonly string[], timezone: string): NotificationMoment[] {
  return times.map((time) => ({ dayKey, time, at: fromDayKeyTime(dayKey, time, timezone) }));
}

/**
 * What to do at `now` for a person's configured times. Looks at yesterday (a
 * late moment may still be inside its grace period just after midnight),
 * today and tomorrow, so the next moment is found across day boundaries.
 * Older moments are never caught up.
 */
export function planNotifications(now: Date, timezone: string, times: readonly string[]): NotificationPlan {
  const todayKey = toDayKey(now, timezone);
  const moments = [addDays(todayKey, -1), todayKey, addDays(todayKey, 1)]
    .flatMap((dayKey) => momentsOnDay(dayKey, times, timezone))
    .sort((a, b) => a.at.getTime() - b.at.getTime());
  const nowMs = now.getTime();
  return {
    deliver: moments.filter((m) => m.at.getTime() <= nowMs && nowMs < m.at.getTime() + GRACE_MS),
    next: moments.find((m) => m.at.getTime() > nowMs) ?? null,
  };
}

/** Milliseconds until the timer should look again; null when there is nothing to wait for. */
export function nextCheckDelay(now: Date, plan: NotificationPlan): number | null {
  if (!plan.next) return null;
  return Math.max(0, Math.min(plan.next.at.getTime() - now.getTime(), MAX_CHECK_INTERVAL_MS));
}

export interface NotificationContent {
  title: string;
  body: string;
  /** Open tasks assigned to the person, due today. */
  today: number;
  /** Open tasks assigned to the person, due before today. */
  overdue: number;
}

type OccurrenceLike = Pick<OccurrenceView, 'status' | 'date' | 'assigneeId' | 'taskNameSnapshot'>;

/**
 * The person's open tasks for today and their overdue ones. Null when there is
 * nothing open: an empty day sends no notification.
 */
export function buildNotificationContent(
  occurrences: readonly OccurrenceLike[],
  personId: string,
  todayKey: string,
): NotificationContent | null {
  const collator = new Intl.Collator(getLocale());
  const open = occurrences.filter((o) => o.status === 'open' && o.assigneeId === personId && o.date <= todayKey);
  const byName = (a: OccurrenceLike, b: OccurrenceLike) => collator.compare(a.taskNameSnapshot, b.taskNameSnapshot);
  const today = open.filter((o) => o.date === todayKey).sort(byName);
  const overdue = open.filter((o) => o.date < todayKey).sort((a, b) => a.date.localeCompare(b.date) || byName(a, b));
  if (today.length + overdue.length === 0) return null;

  const names = [...today, ...overdue].map((o) => o.taskNameSnapshot);
  const listed = names.slice(0, MAX_LISTED_TASKS);
  const rest = names.length - listed.length;
  const body = rest > 0 ? `${listed.join(', ')} ${format('notify.browser.more', { count: rest })}` : listed.join(', ');

  const parts: string[] = [];
  if (today.length > 0) {
    parts.push(format(today.length === 1 ? 'notify.browser.today.one' : 'notify.browser.today.other', { count: today.length }));
  }
  if (overdue.length > 0) {
    parts.push(format(overdue.length === 1 ? 'notify.browser.overdue.one' : 'notify.browser.overdue.other', { count: overdue.length }));
  }
  return { title: `${t('app.name')}: ${parts.join(', ')}`, body, today: today.length, overdue: overdue.length };
}

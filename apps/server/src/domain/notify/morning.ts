import { fromDayKey, today } from '@huishoudplanner/shared';
import type { FastifyBaseLogger } from 'fastify';
import type { Db } from 'mongodb';
import type { Clock } from '../../clock.ts';
import { findOccurrences } from '../../data/occurrences.ts';
import { getSettings } from '../../data/settings.ts';
import { listUsers } from '../../data/users.ts';
import { computeDueList, summarizeDue } from '../due.ts';
import type { Notifier } from './notifier.ts';

export const MORNING_TITLE = 'Keep the House Clean';

export interface MorningCounts {
  name: string;
  /** Open occurrences today assigned to this person. */
  mine: number;
  /** Open occurrences today for "wie dan ook" (same number for everyone). */
  anyone: number;
  /** Household-wide overdue tasks from the due engine. */
  overdue: number;
}

const taken = (n: number) => (n === 1 ? '1 taak' : `${n} taken`);

/** Dutch morning text; null when there is nothing to report. */
export function morningMessage(c: MorningCounts): string | null {
  if (c.mine === 0 && c.anyone === 0 && c.overdue === 0) return null;
  const parts = [`Goedemorgen ${c.name}!`];
  if (c.mine > 0 && c.anyone > 0) parts.push(`Vandaag staan er ${taken(c.mine)} voor je klaar en ${taken(c.anyone)} voor wie dan ook.`);
  else if (c.mine > 0) parts.push(`Vandaag staan er ${taken(c.mine)} voor je klaar.`);
  else if (c.anyone > 0) parts.push(`Vandaag staan er ${taken(c.anyone)} klaar voor wie dan ook.`);
  else parts.push('Vandaag staat er niets voor je gepland.');
  if (c.overdue > 0) parts.push(c.overdue === 1 ? '1 taak is achterstallig.' : `${c.overdue} taken zijn achterstallig.`);
  return parts.join(' ');
}

export interface MorningResult {
  status: 'disabled' | 'done' | 'error';
  date: string | null;
  sent: number;
  failed: number;
  /** Users with nothing to report. */
  quiet: number;
}

export interface MorningDeps {
  db: Db;
  clock: Clock;
  log: FastifyBaseLogger;
  notifier: Notifier | null;
}

/** One message per active user. Never throws: every failure is logged and counted. */
export async function runMorningNotify(deps: MorningDeps): Promise<MorningResult> {
  const result: MorningResult = { status: 'disabled', date: null, sent: 0, failed: 0, quiet: 0 };
  if (!deps.notifier) return result;
  const notifier = deps.notifier;

  let messages: { userId: string; counts: MorningCounts; body: string | null }[];
  try {
    const now = deps.clock.now();
    const settings = await getSettings(deps.db);
    const tz = settings?.timezone;
    const todayKey = today(tz, now);
    const [users, occurrences, due] = await Promise.all([
      listUsers(deps.db, { active: true }),
      findOccurrences(deps.db, { status: 'open', date: fromDayKey(todayKey, tz) }),
      computeDueList(deps.db, now),
    ]);
    const overdue = summarizeDue(due.items).overdue;
    const anyone = occurrences.filter((o) => o.assigneeId === null).length;
    result.date = todayKey;
    messages = users.map((user) => {
      const counts: MorningCounts = {
        name: user.name,
        mine: occurrences.filter((o) => o.assigneeId?.equals(user._id)).length,
        anyone,
        overdue,
      };
      return { userId: user._id.toHexString(), counts, body: morningMessage(counts) };
    });
  } catch (err) {
    deps.log.error({ err }, 'morning notification failed');
    return { ...result, status: 'error' };
  }

  for (const message of messages) {
    if (message.body === null) {
      result.quiet += 1;
      continue;
    }
    try {
      await notifier.send({
        title: MORNING_TITLE,
        body: message.body,
        data: {
          kind: 'morning',
          date: result.date,
          userId: message.userId,
          userName: message.counts.name,
          openToday: message.counts.mine,
          openTodayAnyone: message.counts.anyone,
          overdue: message.counts.overdue,
        },
      });
      result.sent += 1;
    } catch (err) {
      result.failed += 1;
      deps.log.error({ err, userId: message.userId, notifier: notifier.type }, 'morning notification failed');
    }
  }
  result.status = 'done';
  deps.log.info({ ...result, notifier: notifier.type }, 'morning notification completed');
  return result;
}

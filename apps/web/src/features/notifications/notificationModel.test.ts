import type { Occurrence } from '../../api/index.ts';
import { describe, expect, it } from 'vitest';
import { applyLanguage } from '../../i18n/runtime.ts';
import { makeOccurrenceV2 } from '../../test/render.tsx';
import {
  buildNotificationContent,
  GRACE_MS,
  MAX_CHECK_INTERVAL_MS,
  nextCheckDelay,
  planNotifications,
} from './notificationModel.ts';

const AMS = 'Europe/Amsterdam';
const at = (iso: string) => new Date(iso);
const iso = (date: Date | undefined) => date?.toISOString();

describe('planNotifications', () => {
  it('finds the next moment later today', () => {
    // 07:00 in Amsterdam (CEST)
    const plan = planNotifications(at('2026-09-16T05:00:00Z'), AMS, ['18:30', '08:00']);
    expect(plan.deliver).toEqual([]);
    expect(plan.next).toMatchObject({ dayKey: '2026-09-16', time: '08:00' });
    expect(iso(plan.next?.at)).toBe('2026-09-16T06:00:00.000Z');
  });

  it('continues with the first moment of tomorrow after the last one of today', () => {
    // 19:00 in Amsterdam
    const plan = planNotifications(at('2026-09-16T17:00:00Z'), AMS, ['08:00', '18:30']);
    expect(plan.next).toMatchObject({ dayKey: '2026-09-17', time: '08:00' });
    expect(iso(plan.next?.at)).toBe('2026-09-17T06:00:00.000Z');
  });

  it('delivers a moment at its exact time and for ten minutes after it', () => {
    const times = ['08:00', '18:30'];
    const exact = planNotifications(at('2026-09-16T06:00:00.000Z'), AMS, times);
    expect(exact.deliver.map((m) => m.time)).toEqual(['08:00']);
    expect(exact.next?.time).toBe('18:30');

    const lastSecond = planNotifications(at('2026-09-16T06:09:59.999Z'), AMS, times);
    expect(lastSecond.deliver.map((m) => m.time)).toEqual(['08:00']);

    const expired = planNotifications(new Date(at('2026-09-16T06:00:00.000Z').getTime() + GRACE_MS), AMS, times);
    expect(expired.deliver).toEqual([]);
    expect(expired.next?.time).toBe('18:30');
  });

  it('does not catch up on earlier moments of the day', () => {
    // 12:00 in Amsterdam: 08:00 is long gone, 18:30 is still to come.
    const plan = planNotifications(at('2026-09-16T10:00:00Z'), AMS, ['08:00', '18:30']);
    expect(plan.deliver).toEqual([]);
    expect(plan.next?.time).toBe('18:30');
  });

  it('delivers several moments that are all inside the grace period, oldest first', () => {
    const plan = planNotifications(at('2026-09-16T06:07:00Z'), AMS, ['08:05', '08:00', '08:10']);
    expect(plan.deliver.map((m) => m.time)).toEqual(['08:00', '08:05']);
    expect(plan.next?.time).toBe('08:10');
  });

  it('keeps a late moment of yesterday deliverable just after midnight', () => {
    // 00:03 on 17 September in Amsterdam
    const plan = planNotifications(at('2026-09-16T22:03:00Z'), AMS, ['23:55']);
    expect(plan.deliver).toMatchObject([{ dayKey: '2026-09-16', time: '23:55' }]);
    expect(plan.next).toMatchObject({ dayKey: '2026-09-17', time: '23:55' });
  });

  it('has nothing to do without configured times', () => {
    expect(planNotifications(at('2026-09-16T05:00:00Z'), AMS, [])).toEqual({ deliver: [], next: null });
  });

  describe('daylight saving', () => {
    it('keeps a morning moment at the same local time across spring forward', () => {
      // 21:00 on Saturday 28 March; the next day has 23 hours.
      const plan = planNotifications(at('2026-03-28T20:00:00Z'), AMS, ['08:00']);
      expect(plan.next).toMatchObject({ dayKey: '2026-03-29', time: '08:00' });
      expect(iso(plan.next?.at)).toBe('2026-03-29T06:00:00.000Z');
    });

    it('delivers a moment that the spring-forward gap skips, after the gap', () => {
      // 02:30 does not exist on 29 March 2026; it is shown at 03:30 local = 01:30Z.
      const before = planNotifications(at('2026-03-29T01:00:00Z'), AMS, ['02:30']);
      expect(before.deliver).toEqual([]);
      expect(iso(before.next?.at)).toBe('2026-03-29T01:30:00.000Z');
      const after = planNotifications(at('2026-03-29T01:35:00Z'), AMS, ['02:30']);
      expect(after.deliver).toMatchObject([{ dayKey: '2026-03-29', time: '02:30' }]);
    });

    it('keeps a morning moment at the same local time across fall back', () => {
      // 25-hour day: 08:00 local is 07:00Z.
      const plan = planNotifications(at('2026-10-24T20:00:00Z'), AMS, ['08:00']);
      expect(plan.next).toMatchObject({ dayKey: '2026-10-25', time: '08:00' });
      expect(iso(plan.next?.at)).toBe('2026-10-25T07:00:00.000Z');
    });

    it('delivers an ambiguous fall-back time once, at its first occurrence', () => {
      // 02:30 happens twice on 25 October 2026 (00:30Z and 01:30Z); the first one counts.
      const first = planNotifications(at('2026-10-25T00:31:00Z'), AMS, ['02:30']);
      expect(first.deliver).toHaveLength(1);
      const second = planNotifications(at('2026-10-25T01:31:00Z'), AMS, ['02:30']);
      expect(second.deliver).toEqual([]);
    });
  });

  it('reads the same times in another household timezone', () => {
    const now = at('2026-09-16T05:00:00Z');
    expect(iso(planNotifications(now, AMS, ['08:00']).next?.at)).toBe('2026-09-16T06:00:00.000Z');
    // 01:00 in New York: 08:00 there is later today.
    const newYork = planNotifications(now, 'America/New_York', ['08:00']);
    expect(newYork.next).toMatchObject({ dayKey: '2026-09-16' });
    expect(iso(newYork.next?.at)).toBe('2026-09-16T12:00:00.000Z');
    // 14:00 in Tokyo: today's 08:00 is past, so it is tomorrow's.
    const tokyo = planNotifications(now, 'Asia/Tokyo', ['08:00']);
    expect(tokyo.next).toMatchObject({ dayKey: '2026-09-17' });
    expect(iso(tokyo.next?.at)).toBe('2026-09-16T23:00:00.000Z');
  });
});

describe('nextCheckDelay', () => {
  it('waits exactly until a moment that is close', () => {
    const now = at('2026-09-16T05:59:30Z');
    expect(nextCheckDelay(now, planNotifications(now, AMS, ['08:00']))).toBe(30_000);
  });

  it('wakes up at least every minute, to notice clock changes and sleep', () => {
    const now = at('2026-09-16T05:00:00Z');
    expect(nextCheckDelay(now, planNotifications(now, AMS, ['08:00']))).toBe(MAX_CHECK_INTERVAL_MS);
  });

  it('does not wait without a next moment', () => {
    expect(nextCheckDelay(at('2026-09-16T05:00:00Z'), { deliver: [], next: null })).toBeNull();
  });
});

describe('buildNotificationContent', () => {
  const ME = 'a00000000000000000000001';
  const OTHER = 'b00000000000000000000002';
  const TODAY = '2026-09-16';
  const occ = (id: string, overrides: Partial<Occurrence>) => makeOccurrenceV2({ id, assigneeId: ME, ...overrides });

  it('sends nothing on an empty day', () => {
    expect(buildNotificationContent([], ME, TODAY)).toBeNull();
  });

  it('sends nothing when only other people, finished, skipped or future tasks remain', () => {
    const occurrences = [
      occ('o1', { assigneeId: OTHER, taskNameSnapshot: 'Van een ander' }),
      occ('o2', { assigneeId: null, taskNameSnapshot: 'Wie dan ook' }),
      occ('o3', { status: 'done', taskNameSnapshot: 'Klaar' }),
      occ('o4', { status: 'skipped', date: '2026-09-10', taskNameSnapshot: 'Overgeslagen' }),
      occ('o5', { date: '2026-09-17', taskNameSnapshot: 'Morgen' }),
    ];
    expect(buildNotificationContent(occurrences, ME, TODAY)).toBeNull();
  });

  it("summarises the person's tasks for today and their overdue ones", () => {
    const content = buildNotificationContent(
      [
        occ('o1', { date: TODAY, taskNameSnapshot: 'Stofzuigen' }),
        occ('o2', { date: TODAY, taskNameSnapshot: 'Afwassen' }),
        occ('o3', { date: '2026-09-14', taskNameSnapshot: 'Badkamer poetsen' }),
        occ('o4', { date: TODAY, assigneeId: OTHER, taskNameSnapshot: 'Niet van mij' }),
      ],
      ME,
      TODAY,
    );
    expect(content).toEqual({
      title: 'Keep the House Clean: 2 taken vandaag, 1 achterstallig',
      body: 'Afwassen, Stofzuigen, Badkamer poetsen',
      today: 2,
      overdue: 1,
    });
  });

  it('uses singular wording and leaves out an empty part', () => {
    expect(buildNotificationContent([occ('o1', { taskNameSnapshot: 'Afwassen' })], ME, TODAY)?.title).toBe(
      'Keep the House Clean: 1 taak vandaag',
    );
    expect(
      buildNotificationContent([occ('o1', { date: '2026-09-01', taskNameSnapshot: 'Afwassen' })], ME, TODAY)?.title,
    ).toBe('Keep the House Clean: 1 achterstallig');
  });

  it('lists at most five names and counts the rest', () => {
    const occurrences = ['A', 'B', 'C', 'D', 'E', 'F', 'G'].map((name, i) => occ(`o${i}`, { taskNameSnapshot: `Taak ${name}` }));
    expect(buildNotificationContent(occurrences, ME, TODAY)?.body).toBe('Taak A, Taak B, Taak C, Taak D, Taak E +2 meer');
    expect(buildNotificationContent(occurrences.slice(0, 5), ME, TODAY)?.body).toBe('Taak A, Taak B, Taak C, Taak D, Taak E');
  });

  it('writes English when the language is English', () => {
    applyLanguage('en');
    const occurrences = ['A', 'B', 'C', 'D', 'E', 'F'].map((name, i) =>
      occ(`o${i}`, { date: i === 5 ? '2026-09-10' : TODAY, taskNameSnapshot: `Task ${name}` }),
    );
    expect(buildNotificationContent(occurrences, ME, TODAY)).toMatchObject({
      title: 'Keep the House Clean: 5 tasks today, 1 overdue',
      body: 'Task A, Task B, Task C, Task D, Task E +1 more',
    });
  });
});

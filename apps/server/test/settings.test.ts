import { DEFAULT_INTERVALS, type Interval } from '@huishoudplanner/shared';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { getSettings, StaleBonusScheduleError, updateSettings } from '../src/data/settings.ts';
import { createTask } from '../src/data/tasks.ts';
import type { UserDoc } from '../src/data/users.ts';
import { findInterval } from '../src/domain/intervals.ts';
import { captureWrites, expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

let t: TestApp;
let p1: UserDoc;

beforeAll(async () => {
  t = await createTestApp();
  [p1] = await seededUsers(t);
});

afterAll(async () => {
  await t.close();
});

function patchSettings(payload: Record<string, unknown>) {
  return t.app.inject({ method: 'PATCH', url: '/api/settings', headers: asProfile(p1), payload });
}

describe('settings API', () => {
  it('returns the seeded settings', async () => {
    const res = await t.app.inject({ method: 'GET', url: '/api/settings' });
    expect(res.statusCode).toBe(200);
    expect(res.json()).toMatchObject({
      cycleAnchorDate: '2026-09-14',
      weekStartsOn: 1,
      timezone: 'Europe/Amsterdam',
      intervals: DEFAULT_INTERVALS,
      aiProvider: { type: 'none' },
    });
  });

  it('rejects an anchor that is not a Monday', async () => {
    const res = await patchSettings({ cycleAnchorDate: '2026-09-15' });
    expect(res.statusCode).toBe(400);
    expect(res.json<{ details: { field: string; message: string }[] }>().details).toEqual([
      { field: 'cycleAnchorDate', message: 'anchor_not_monday' },
    ]);
  });

  it('updates the anchor with an audited diff', async () => {
    const { result, entries } = await expectAudited(t, () => patchSettings({ cycleAnchorDate: '2026-09-07' }), {
      entity: 'settings',
      action: 'update',
      source: 'ui',
      count: 1,
    });
    expect(result.statusCode).toBe(200);
    expect(entries[0]!.before).toEqual({ cycleAnchorDate: '2026-09-14' });
    expect(entries[0]!.after).toEqual({ cycleAnchorDate: '2026-09-07' });
  });

  it('manages vacation ranges and rejects inverted ones', async () => {
    const ranges = [{ from: '2026-12-24', to: '2026-12-31' }];
    const { entries } = await expectAudited(t, () => patchSettings({ vacationRanges: ranges }), {
      entity: 'settings',
      action: 'update',
      count: 1,
    });
    expect(entries[0]!.after).toEqual({ vacationRanges: ranges });
    const inverted = await patchSettings({ vacationRanges: [{ from: '2026-12-31', to: '2026-12-24' }] });
    expect(inverted.statusCode).toBe(400);
  });

  it('adds a new interval "year" that is then usable for a task, without code changes', async () => {
    const year: Interval = { key: 'year', label: '1x per jaar', perCycle: null, periodDays: 365 };
    const { result } = await expectAudited(t, () => patchSettings({ intervals: [...DEFAULT_INTERVALS, year] }), {
      entity: 'settings',
      action: 'update',
      count: 1,
    });
    expect(result.statusCode).toBe(200);

    const settings = await getSettings(t.db);
    expect(findInterval(settings!.intervals, 'year')).toEqual(year);

    const room = await seededRoom(t, 'Slaapkamer');
    const task = await createTask(t.systemCtx(), {
      name: 'Matras keren',
      roomId: room._id,
      intervalKey: 'year',
      durationMinutes: 15,
      defaultAssigneeId: null,
      notes: '',
      tags: [],
    });
    expect(task.intervalKey).toBe('year');
  });

  it('allows removing an unused interval but blocks removing one in use (409)', async () => {
    // 'year' is used by the task created above; 'quarter' is unused
    const withoutQuarter = [...DEFAULT_INTERVALS.filter((i) => i.key !== 'quarter'), {
      key: 'year',
      label: '1x per jaar',
      perCycle: null,
      periodDays: 365,
    }];
    const ok = await patchSettings({ intervals: withoutQuarter });
    expect(ok.statusCode).toBe(200);

    const before = await getSettings(t.db);
    const blocked = await patchSettings({ intervals: DEFAULT_INTERVALS.filter((i) => i.key !== 'quarter') });
    expect(blocked.statusCode).toBe(409);
    expect(blocked.json()).toMatchObject({ code: 'interval_in_use', details: { keys: ['year'] } });
    expect((await getSettings(t.db))?.intervals).toEqual(before?.intervals);
  });

  it('rejects duplicate interval keys', async () => {
    const res = await patchSettings({ intervals: [...DEFAULT_INTERVALS, DEFAULT_INTERVALS[0]] });
    expect(res.statusCode).toBe(400);
  });

  it('stores an Ollama timeout between 10 and 900 seconds', async () => {
    const ok = await patchSettings({
      aiProvider: { type: 'ollama', endpoint: 'http://host.docker.internal:11434', model: 'qwen3:8b', timeoutSeconds: 240 },
    });
    expect(ok.statusCode).toBe(200);
    expect(ok.json().aiProvider).toEqual({
      type: 'ollama',
      endpoint: 'http://host.docker.internal:11434',
      model: 'qwen3:8b',
      timeoutSeconds: 240,
    });
    expect((await patchSettings({ aiProvider: { type: 'ollama', model: 'qwen3:8b', timeoutSeconds: 9 } })).statusCode).toBe(400);
    expect((await patchSettings({ aiProvider: { type: 'ollama', model: 'qwen3:8b', timeoutSeconds: 901 } })).statusCode).toBe(400);
  });

  it('stores AI prompt templates only when the dynamic placeholders remain present', async () => {
    const action = { system: 'Systeem {{schema}}', user: 'Gebruiker {{input}}' };
    const aiPromptTemplates = {
      planProposal: action,
      planRebalance: action,
      taskSuggestions: action,
      planExplanation: action,
    };
    const ok = await patchSettings({ aiPromptTemplates });
    expect(ok.statusCode, ok.body).toBe(200);
    expect(ok.json().aiPromptTemplates).toEqual(aiPromptTemplates);

    const invalid = await patchSettings({
      aiPromptTemplates: { ...aiPromptTemplates, planProposal: { system: 'zonder schema', user: 'Gebruiker {{input}}' } },
    });
    expect(invalid.statusCode).toBe(400);
    expect(invalid.json<{ details: { field: string; message: string }[] }>().details).toContainEqual({
      field: 'aiPromptTemplates.planProposal.system',
      message: 'schema_placeholder_required',
    });
  });

  it('does not write or audit when nothing changes', async () => {
    const { result } = await expectAudited(
      t,
      () => patchSettings({ promoteThreshold: 2 }),
      { entity: 'settings', action: 'update', count: 0 },
    );
    expect(result.statusCode).toBe(200);
  });
});

describe('period bonuses (ADR-0012)', () => {
  const AMOUNTS = { weekDone: 5, weekOnTime: 3, cycleDone: 20, cycleOnTime: 10 };
  const ZERO = { weekDone: 0, weekOnTime: 0, cycleDone: 0, cycleOnTime: 0 };
  let bonuses: TestApp;
  let admin: UserDoc;
  let member: UserDoc;

  beforeAll(async () => {
    bonuses = await createTestApp({ now: '2026-09-16T08:00:00.000Z' });
    [admin, member] = await seededUsers(bonuses);
  });

  afterAll(async () => {
    await bonuses.close();
  });

  const patch = (payload: Record<string, unknown>, actor: UserDoc = admin) =>
    bonuses.app.inject({ method: 'PATCH', url: '/api/settings', headers: asProfile(actor), payload });
  const schedule = async () =>
    (await bonuses.app.inject({ method: 'GET', url: '/api/settings' })).json<{ bonusSchedule: unknown[] }>().bonusSchedule;

  it('returns an empty schedule by default, so bonuses are disabled', async () => {
    expect(await schedule()).toEqual([]);
    expect((await getSettings(bonuses.db))!.bonusSchedule).toBeUndefined();
  });

  it('writes and audits nothing for amounts that equal the amounts in force, also when all are 0', async () => {
    const capture = await captureWrites(bonuses, () => patch({ periodBonuses: ZERO }));
    expect(capture.result.statusCode, capture.result.body).toBe(200);
    expect(capture.writes).toEqual([]);
    expect(capture.auditInserts).toBe(0);
  });

  it('writes a row from today with an audited before and after, for administrators only', async () => {
    const denied = await patch({ periodBonuses: AMOUNTS }, member);
    expect(denied.statusCode).toBe(403);
    expect(await schedule()).toEqual([]);

    const { result, entries } = await expectAudited(bonuses, () => patch({ periodBonuses: AMOUNTS }), {
      entity: 'settings',
      action: 'update',
      source: 'ui',
      count: 1,
    });
    expect(result.statusCode, result.body).toBe(200);
    expect(result.json()).toMatchObject({ bonusSchedule: [{ from: '2026-09-16', ...AMOUNTS }] });
    expect(entries[0]!.before).toEqual({ bonusSchedule: [] });
    expect(entries[0]!.after).toEqual({ bonusSchedule: [{ from: '2026-09-16', ...AMOUNTS }] });
    expect(await schedule()).toEqual([{ from: '2026-09-16', ...AMOUNTS }]);
  });

  it('writes nothing for the same amounts again, and replaces the row that starts today', async () => {
    const same = await captureWrites(bonuses, () => patch({ periodBonuses: AMOUNTS }));
    expect(same.writes).toEqual([]);
    expect(same.auditInserts).toBe(0);

    const changed = { ...AMOUNTS, weekDone: 6 };
    const { entries } = await expectAudited(bonuses, () => patch({ periodBonuses: changed }), { entity: 'settings', action: 'update', count: 1 });
    expect(entries[0]!.before).toEqual({ bonusSchedule: [{ from: '2026-09-16', ...AMOUNTS }] });
    expect(await schedule()).toEqual([{ from: '2026-09-16', ...changed }]);
  });

  it('keeps the earlier rows and adds a row on a later day, so an ended period keeps its amounts', async () => {
    bonuses.clock.set('2026-09-30T08:00:00.000Z');
    const next = { weekDone: 1, weekOnTime: 2, cycleDone: 3, cycleOnTime: 4 };
    expect((await patch({ periodBonuses: next })).statusCode).toBe(200);
    expect(await schedule()).toEqual([
      { from: '2026-09-16', ...AMOUNTS, weekDone: 6 },
      { from: '2026-09-30', ...next },
    ]);
    // Switching a kind off is an amount of 0, which is also a row.
    expect((await patch({ periodBonuses: { ...next, cycleOnTime: 0 } })).statusCode).toBe(200);
    expect((await schedule())[1]).toMatchObject({ from: '2026-09-30', cycleOnTime: 0 });
  });

  it('rejects amounts outside 0 to 1000 and incomplete or fractional amounts', async () => {
    for (const periodBonuses of [{ ...AMOUNTS, weekDone: 1001 }, { ...AMOUNTS, weekDone: -1 }, { ...AMOUNTS, cycleOnTime: 1.5 }, { weekDone: 1 }]) {
      const res = await patch({ periodBonuses });
      expect(res.statusCode, JSON.stringify(periodBonuses)).toBe(400);
      expect(res.json<{ code: string }>().code).toBe('validation_error');
    }
    expect((await patch({ periodBonuses: { ...AMOUNTS, weekDone: 1000 } })).statusCode).toBe(200);
    // The schedule itself is never accepted from a client.
    const direct = await patch({ bonusSchedule: [{ from: '2026-01-01', ...AMOUNTS }] });
    expect(direct.statusCode).toBe(200);
    expect((await schedule()).some((row) => (row as { from: string }).from === '2026-01-01')).toBe(false);
  });

  it('writes the schedule as a compare-and-set on the schedule it was computed from', async () => {
    const t2 = await createTestApp({ now: '2026-09-16T08:00:00.000Z' });
    try {
      const ctx = t2.systemCtx();
      const row = { from: '2026-09-16', ...AMOUNTS };
      // Nothing is stored yet: a write that expects an earlier schedule is refused and writes nothing.
      await expect(updateSettings(ctx, { bonusSchedule: [row] }, { basedOnBonusSchedule: { rows: [row] } })).rejects.toBeInstanceOf(StaleBonusScheduleError);
      expect((await getSettings(t2.db))!.bonusSchedule).toBeUndefined();
      await updateSettings(ctx, { bonusSchedule: [row] }, { basedOnBonusSchedule: { rows: undefined } });
      expect((await getSettings(t2.db))!.bonusSchedule).toEqual([row]);
      // The same expectation is stale now.
      const other = { ...row, weekDone: 9 };
      await expect(updateSettings(ctx, { bonusSchedule: [other] }, { basedOnBonusSchedule: { rows: undefined } })).rejects.toBeInstanceOf(StaleBonusScheduleError);
      await updateSettings(ctx, { bonusSchedule: [other] }, { basedOnBonusSchedule: { rows: [row] } });
      expect((await getSettings(t2.db))!.bonusSchedule).toEqual([other]);
    } finally {
      await t2.close();
    }
  });

  it('answers 409 bonus_schedule_conflict when two administrators set amounts at the same time, and keeps one schedule', async () => {
    const t2 = await createTestApp({ now: '2026-09-16T08:00:00.000Z' });
    try {
      const [first] = await seededUsers(t2);
      const send = (weekDone: number) =>
        t2.app.inject({ method: 'PATCH', url: '/api/settings', headers: asProfile(first!), payload: { periodBonuses: { ...AMOUNTS, weekDone } } });
      const answers = await Promise.all([send(1), send(2), send(3), send(4), send(5), send(6)]);
      const statuses = answers.map((answer) => answer.statusCode);
      expect(statuses.every((status) => status === 200 || status === 409)).toBe(true);
      expect(statuses).toContain(200);
      for (const answer of answers.filter((a) => a.statusCode === 409)) {
        expect(answer.json<{ code: string }>().code).toBe('bonus_schedule_conflict');
      }
      const schedule = (await getSettings(t2.db))!.bonusSchedule!;
      expect(schedule).toHaveLength(1);
      expect(schedule[0]!.from).toBe('2026-09-16');
    } finally {
      await t2.close();
    }
  });
});

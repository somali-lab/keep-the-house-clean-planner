import { DEFAULT_INTERVALS, type Interval } from '@huishoudplanner/shared';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { getSettings } from '../src/data/settings.ts';
import { createTask } from '../src/data/tasks.ts';
import type { UserDoc } from '../src/data/users.ts';
import { findInterval } from '../src/domain/intervals.ts';
import { expectAudited } from './helpers/audit.ts';
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

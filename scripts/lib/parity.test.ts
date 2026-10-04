import { describe, expect, it } from 'vitest';
import { httpReader } from './httpReader.ts';
import {
  compareAwards,
  compareBalances,
  compareDue,
  compareHealth,
  compareLists,
  compareOccurrences,
  compareSettings,
  deepDiff,
  formatReport,
  normalize,
  normalizeInstant,
  readAllV2,
  runParity,
  unwrapV1,
  type Reader,
} from './parity.ts';

const ANNA = 'aaaaaaaaaaaaaaaaaaaaaaa1';
const BRAM = 'aaaaaaaaaaaaaaaaaaaaaaa2';
const ROOM = 'bbbbbbbbbbbbbbbbbbbbbbb1';
const TASK1 = 'cccccccccccccccccccccc01';
const TASK2 = 'cccccccccccccccccccccc02';
const PLAN = 'dddddddddddddddddddddd01';
const CYCLE = 'eeeeeeeeeeeeeeeeeeeeeee1';
const OCC1 = 'ffffffffffffffffffffff01';
const OCC2 = 'ffffffffffffffffffffff02';
const BADGE = '111111111111111111111111';

/** The same household as the two applications print it: v1 with `_id` and `Z`, v2 with `id`, `+00:00`, `version` and nulls. */
function fixture() {
  const users1 = [
    {
      _id: ANNA,
      name: 'Anna',
      color: '#2563eb',
      active: true,
      role: 'admin',
      createdAt: '2026-09-16T08:00:00.000Z',
      updatedAt: '2026-09-16T08:00:00.000Z',
    },
    {
      _id: BRAM,
      name: 'Bram',
      color: '#db2777',
      active: true,
      role: 'member',
      createdAt: '2026-09-16T08:00:00.000Z',
      updatedAt: '2026-09-16T08:00:00.000Z',
    },
  ];
  const users2 = users1.map(({ _id, ...rest }) => ({
    id: _id,
    ...rest,
    createdAt: '2026-09-16T08:00:00.000+00:00',
    updatedAt: '2026-09-16T08:00:00.000+00:00',
    version: 0,
  }));
  const rooms1 = [{ _id: ROOM, name: 'Keuken', sortOrder: 10, active: true, virtual: false }];
  const rooms2 = [
    { id: ROOM, name: 'Keuken', sortOrder: 10, active: true, virtual: false, version: 2 },
  ];
  const tasks1 = [
    {
      _id: TASK1,
      name: 'Afwas',
      roomId: ROOM,
      intervalKey: 'daily',
      active: true,
      defaultAssigneeId: ANNA,
      lastCompletedAt: '2026-10-01T08:15:30.123Z',
    },
    {
      _id: TASK2,
      name: 'Stofzuigen',
      roomId: ROOM,
      intervalKey: '1w',
      active: false,
      defaultAssigneeId: null,
    },
  ];
  const tasks2 = [
    {
      id: TASK1,
      name: 'Afwas',
      roomId: ROOM,
      intervalKey: 'daily',
      active: true,
      defaultAssigneeId: ANNA,
      lastCompletedAt: '2026-10-01T10:15:30.123+02:00',
      version: 1,
    },
    {
      id: TASK2,
      name: 'Stofzuigen',
      roomId: ROOM,
      intervalKey: '1w',
      active: false,
      defaultAssigneeId: null,
      lastCompletedAt: null,
      version: 0,
    },
  ];
  const plans1 = [
    {
      _id: PLAN,
      name: 'Standaard',
      active: true,
      slots: [{ taskId: TASK1, weekIndex: 0, weekday: 1, assigneeId: ANNA, sortOrder: 0 }],
      rationale: null,
    },
  ];
  const plans2 = [
    {
      id: PLAN,
      name: 'Standaard',
      active: true,
      slots: [{ taskId: TASK1, weekIndex: 0, weekday: 1, assigneeId: ANNA, sortOrder: 0 }],
      rationale: null,
      version: 4,
    },
  ];
  const cycles1 = [
    { _id: CYCLE, index: 0, startDate: '2026-09-28', endDate: '2026-10-25', planId: PLAN },
  ];
  const cycles2 = [
    { id: CYCLE, index: 0, startDate: '2026-09-28', endDate: '2026-10-25', planId: PLAN },
  ];
  const due1 = [
    {
      taskId: TASK1,
      taskName: 'Afwas',
      state: 'overdue',
      ratio: 2,
      nextOccurrence: { id: OCC2, date: '2026-10-06', assigneeId: ANNA },
    },
    { taskId: TASK2, taskName: 'Stofzuigen', state: 'due', ratio: 1, nextOccurrence: null },
  ];
  const due2 = {
    today: '2026-10-04',
    items: due1.map((d) => ({ ...d })),
    nextCursor: null,
    summary: { due: 1, overdue: 1 },
  };
  const occ1 = [
    {
      _id: OCC1,
      taskId: TASK1,
      date: '2026-10-05',
      status: 'done',
      completedAt: '2026-10-05T07:00:00.000Z',
      planId: PLAN,
      requestId: null,
    },
    {
      _id: OCC2,
      taskId: TASK1,
      date: '2026-10-06',
      status: 'open',
      completedAt: null,
      planId: PLAN,
    },
  ];
  const occ2 = occ1.map(({ _id, ...rest }) => ({
    id: _id,
    ...rest,
    completedAt: rest.completedAt && rest.completedAt.replace('Z', '+00:00'),
    requestId: null,
    periodOwnerId: null,
    cycleIndex: 0,
    weekIndex: 0,
  }));
  const balances1 = {
    from: null,
    to: null,
    currencyCode: 'EUR',
    centsPerPoint: 5,
    balances: [
      {
        personId: ANNA,
        points: 50,
        earned: 50,
        redeemed: 0,
        money: { earned: 250, redeemed: 0, balance: 250 },
        executions: 4,
        bonusPoints: 0,
      },
      {
        personId: BRAM,
        points: 80,
        earned: 90,
        redeemed: 10,
        money: null,
        executions: 5,
        bonusPoints: 10,
      },
    ],
  };
  const awards1 = {
    awards: [{ _id: 'a1', badgeId: BADGE, personId: ANNA, awardedAt: '2026-10-01T00:00:00.000Z' }],
  };
  const awards2 = {
    items: [
      {
        id: 'other-id',
        badgeId: BADGE,
        personId: ANNA,
        awardedAt: '2026-10-01T02:00:00.000+02:00',
      },
    ],
    nextCursor: null,
  };
  const badges1 = {
    badges: [
      {
        _id: BADGE,
        name: 'Starter',
        rule: { type: 'executions', taskIds: [], threshold: 2 },
        active: true,
        image: null,
      },
    ],
  };
  const badges2 = {
    items: [
      {
        id: BADGE,
        name: 'Starter',
        rule: { type: 'executions', taskIds: [], threshold: 2 },
        active: true,
        image: null,
        version: 1,
      },
    ],
    nextCursor: null,
  };
  const settings1 = {
    _id: '000000000000000000000001',
    cycleAnchorDate: '2026-09-28',
    weekStartsOn: 1,
    timezone: 'Europe/Amsterdam',
    intervals: [{ key: 'daily', label: 'Dagelijks', periodDays: 1, perCycle: 28 }],
    aiProvider: { type: 'none' },
    completionControl: 'circle',
    promoteThreshold: 3,
    currencyCode: 'EUR',
    centsPerPoint: 5,
    bonusSchedule: [
      { from: '2026-10-05', weekDone: 10, weekOnTime: 5, cycleDone: 20, cycleOnTime: 10 },
    ],
  };
  const settings2 = {
    id: '000000000000000000000001',
    cycleAnchorDate: '2026-09-28',
    weekStartsOn: 1,
    timezone: 'Europe/Amsterdam',
    vacationRanges: [],
    intervals: settings1.intervals,
    aiProvider: { type: 'none', endpoint: null, model: null, timeoutSeconds: null },
    aiPrompts: null,
    completionControl: 'circle',
    promoteThreshold: 3,
    dismissedPromotions: [],
    bonusSchedule: [{ ...settings1.bonusSchedule[0]!, startsInFuture: false }],
    bonusesInForce: { weekDone: 10 },
    bonusFloor: null,
    currencyCode: 'EUR',
    centsPerPoint: 5,
    rewardGoals: null,
    version: 7,
  };
  return {
    users1,
    users2,
    rooms1,
    rooms2,
    tasks1,
    tasks2,
    plans1,
    plans2,
    cycles1,
    cycles2,
    due1,
    due2,
    occ1,
    occ2,
    balances1,
    awards1,
    awards2,
    badges1,
    badges2,
    settings1,
    settings2,
  };
}

type F = ReturnType<typeof fixture>;

/** Readers over canned answers; v2 lists are served in pages of one so that paging is exercised. */
function readers(
  f: F,
  changes: { v1?: Record<string, unknown>; v2?: Record<string, unknown> } = {},
) {
  const v1: Record<string, unknown> = {
    '/health': { status: 'ok', mongo: 'ok' },
    '/users': f.users1,
    '/rooms': f.rooms1,
    '/tasks': f.tasks1,
    '/cycle-plans': f.plans1,
    '/cycles': f.cycles1,
    '/settings': f.settings1,
    '/due': f.due1,
    '/occurrences?from=2026-09-28&to=2026-10-25': f.occ1,
    '/points/balances': f.balances1,
    '/points/balances?from=2026-09-28&to=2026-10-25': f.balances1,
    '/badges': f.badges1,
    '/badges/awards': f.awards1,
    ...changes.v1,
  };
  const v2lists: Record<string, unknown[]> = {
    '/users': f.users2,
    '/rooms': f.rooms2,
    '/tasks': f.tasks2,
    '/cycle-plans': f.plans2,
    '/cycles': f.cycles2,
    '/due': f.due2.items,
    '/occurrences?from=2026-09-28&to=2026-10-25': f.occ2,
    '/badges': f.badges2.items,
    '/badges/awards': f.awards2.items,
  };
  const v2plain: Record<string, unknown> = {
    '/health': { status: 'ok', version: '1.7.0', database: 'ok' },
    '/settings': f.settings2,
    '/points/balances': f.balances1,
    '/points/balances?from=2026-09-28&to=2026-10-25': f.balances1,
    ...changes.v2,
  };
  const requests: string[] = [];
  const reader = (answers: Record<string, unknown>, lists: Record<string, unknown[]>): Reader => ({
    async get(path) {
      requests.push(path);
      if (path in answers) {
        const answer = answers[path];
        if (answer instanceof Error) throw answer;
        return answer;
      }
      const [base, query = ''] = path.split(/\?(.*)/s);
      const params = new URLSearchParams(query);
      const cursor = Number(params.get('cursor') ?? 0);
      params.delete('limit');
      params.delete('cursor');
      const key = params.size > 0 ? `${base}?${params}` : base!;
      const items = lists[key] ?? lists[`${base}`];
      if (!items) throw new Error(`no answer for ${path}`);
      const next = cursor + 1;
      return {
        items: items.slice(cursor, next),
        nextCursor: next < items.length ? String(next) : null,
        ...(base === '/due' ? { today: '2026-10-04', summary: f.due2.summary } : {}),
      };
    },
  });
  return {
    v1: reader(v1, {}),
    v2: reader(v2plain, { ...v2lists, ...(changes.v2 as Record<string, unknown[]> | undefined) }),
    requests,
  };
}

const RANGE = { from: '2026-09-28', to: '2026-10-25' };

describe('normalisation', () => {
  it('prints the same instant the same way whatever the offset', () => {
    expect(normalizeInstant('2026-10-01T08:15:30.123Z')).toBe('2026-10-01T08:15:30.123Z');
    expect(normalizeInstant('2026-10-01T10:15:30.123+02:00')).toBe('2026-10-01T08:15:30.123Z');
    expect(normalizeInstant('2026-10-01T08:15:30+00:00')).toBe('2026-10-01T08:15:30.000Z');
  });

  it('leaves day keys and other text alone', () => {
    expect(normalizeInstant('2026-10-01')).toBe('2026-10-01');
    expect(normalizeInstant('Stofzuigen 2026-10-01T10:00:00Z')).toBe(
      'Stofzuigen 2026-10-01T10:00:00Z',
    );
  });

  it('turns _id into id at every depth and drops undefined', () => {
    expect(normalize({ _id: 'a', nested: [{ _id: 'b', x: undefined }] })).toEqual({
      id: 'a',
      nested: [{ id: 'b' }],
    });
  });

  it('treats an absent member as null and compares arrays by position', () => {
    expect(deepDiff({ a: null }, {})).toEqual([]);
    expect(deepDiff({ a: [1, 2] }, { a: [1, 3] })).toEqual([{ path: 'a[1]', v1: 2, v2: 3 }]);
    expect(deepDiff({ a: [1] }, { a: [1, 2] })).toEqual([{ path: 'a[1]', v1: undefined, v2: 2 }]);
  });
});

describe('matching data', () => {
  it('is equal in every check, with paging, id, instant, version and null differences ignored', async () => {
    const { v1, v2 } = readers(fixture());
    const report = await runParity(v1, v2, { range: RANGE });
    expect(report.results.map((r) => [r.name, r.differences])).toEqual(
      report.results.map((r) => [r.name, []]),
    );
    expect(report.ok).toBe(true);
    expect(report.results.map((r) => r.name)).toEqual([
      'health',
      'users',
      'rooms',
      'tasks',
      'plans',
      'cycles',
      'settings',
      'due',
      'occurrences 2026-09-28..2026-10-25',
      'balances',
      'badges',
      'awards',
    ]);
    expect(formatReport(report)).toContain('Parity: all 12 checks equal.');
  });

  it('reads every page of the v2 lists', async () => {
    const f = fixture();
    const { v2 } = readers(f);
    const { items } = await readAllV2(v2, '/users', 1);
    expect(items).toHaveLength(2);
  });

  it('only ever asks for reads, and a check can be selected', async () => {
    const { v1, v2, requests } = readers(fixture());
    const report = await runParity(v1, v2, { range: RANGE, only: ['users', 'due'] });
    expect(report.results.map((r) => r.name)).toEqual(['users', 'due']);
    expect(requests.every((path) => path.startsWith('/'))).toBe(true);
  });
});

describe('each kind of difference is reported', () => {
  it('a member that changed', async () => {
    const f = fixture();
    f.tasks2[0]!.name = 'Afwas doen';
    const { v1, v2 } = readers(f);
    const report = await runParity(v1, v2, { range: RANGE, only: ['tasks'] });
    expect(report.ok).toBe(false);
    expect(report.results[0]!.differences).toEqual([
      { kind: 'changed', key: TASK1, path: 'name', v1: 'Afwas', v2: 'Afwas doen' },
    ]);
    const text = formatReport(report);
    expect(text).toContain('DIFF tasks');
    expect(text).toContain(`[${TASK1}] name: v1 "Afwas"  v2 "Afwas doen"`);
    expect(text).toContain('Parity: 1 of 1 checks differ (tasks).');
  });

  it('a record only in v1 and a record only in v2', () => {
    const result = compareLists({
      name: 'rooms',
      v1: [
        { _id: 'r1', name: 'Keuken' },
        { _id: 'r2', name: 'Hal' },
      ],
      v2: [
        { id: 'r1', name: 'Keuken' },
        { id: 'r3', name: 'Garage' },
      ],
      keyOf: (r) => String(r.id),
    });
    expect(result.differences.map((d) => [d.kind, d.key])).toEqual([
      ['only-v1', 'r2'],
      ['only-v2', 'r3'],
    ]);
  });

  it('the order of an ordered list, but not of an unordered one', () => {
    const a = [{ id: '1' }, { id: '2' }, { id: '3' }];
    const b = [{ id: '1' }, { id: '3' }, { id: '2' }];
    const keyOf = (r: { [key: string]: unknown }) => String(r.id);
    expect(compareLists({ name: 'unordered', v1: a, v2: b, keyOf }).differences).toEqual([]);
    const ordered = compareLists({ name: 'ordered', v1: a, v2: b, keyOf, ordered: true });
    expect(ordered.differences).toHaveLength(1);
    expect(ordered.differences[0]).toMatchObject({ kind: 'order', path: 'position 2' });
  });

  it('a record listed twice', () => {
    const result = compareLists({
      name: 'rooms',
      v1: [{ id: '1' }, { id: '1' }],
      v2: [{ id: '1' }],
      keyOf: (r) => String(r.id),
    });
    expect(result.differences.map((d) => d.kind)).toEqual(['duplicate']);
  });

  it('a different number of slots in a plan and a different assignee', async () => {
    const f = fixture();
    f.plans2[0]!.slots.push({
      taskId: TASK2,
      weekIndex: 1,
      weekday: 2,
      assigneeId: BRAM,
      sortOrder: 1,
    });
    f.plans2[0]!.slots[0]!.assigneeId = BRAM;
    const { v1, v2 } = readers(f);
    const report = await runParity(v1, v2, { range: RANGE, only: ['plans'] });
    expect(report.results[0]!.differences.map((d) => d.path)).toEqual([
      'slots[0].assigneeId',
      'slots[1]',
    ]);
  });

  it('an inactive flag, a role and a name of a user, a room and a task', async () => {
    const f = fixture();
    f.users2[1]!.active = false;
    f.users2[0]!.role = 'member';
    f.rooms2[0]!.name = 'Keukentje';
    const { v1, v2 } = readers(f);
    const report = await runParity(v1, v2, { range: RANGE, only: ['users', 'rooms'] });
    expect(report.results.flatMap((r) => r.differences.map((d) => `${r.name}:${d.path}`))).toEqual([
      'users:role',
      'users:active',
      'rooms:name',
    ]);
  });

  it('a due list in another order, with another state, and a summary that does not add up', () => {
    const f = fixture();
    const swapped = compareDue(f.due1, [f.due1[1]!, f.due1[0]!], f.due2.summary);
    expect(swapped.differences.map((d) => d.kind)).toEqual(['order']);
    const state = compareDue(f.due1, [{ ...f.due1[0]!, state: 'due' }, f.due1[1]!], {
      due: 2,
      overdue: 1,
    });
    expect(state.differences.map((d) => `${d.kind}:${d.key}:${d.path}`)).toEqual([
      `changed:${TASK1}:state`,
      'changed:summary:due',
    ]);
  });

  it('an occurrence with another status, a missing one and an extra one, in the range', () => {
    const f = fixture();
    const v2 = [
      { ...f.occ2[0]!, status: 'skipped' },
      { ...f.occ2[1]!, id: 'ffffffffffffffffffffff03' },
    ];
    const result = compareOccurrences(f.occ1, v2, RANGE);
    expect(result.name).toBe('occurrences 2026-09-28..2026-10-25');
    expect(result.note).toBe('by status: done 1, open 1');
    expect(result.differences.map((d) => `${d.kind}:${d.key}`)).toEqual([
      `changed:${OCC1}`,
      `only-v1:${OCC2}`,
      'only-v2:ffffffffffffffffffffff03',
    ]);
    expect(result.differences[0]).toMatchObject({ path: 'status', v1: 'done', v2: 'skipped' });
  });

  it('a points balance per person, the order of the people and the currency', () => {
    const f = fixture();
    const v2 = structuredClone(f.balances1);
    v2.balances[0]!.points = 51;
    v2.centsPerPoint = 6;
    const result = compareBalances(f.balances1, v2);
    expect(result.differences.map((d) => `${d.key}:${d.path}`)).toEqual([
      `${ANNA}:points`,
      'header:centsPerPoint',
    ]);
    const reversed = compareBalances(f.balances1, {
      ...f.balances1,
      balances: [...f.balances1.balances].reverse(),
    });
    expect(reversed.differences.map((d) => d.kind)).toEqual(['order']);
  });

  it('a badge award that is missing for a person, whatever its id and time', () => {
    const f = fixture();
    expect(compareAwards(f.awards1.awards, f.awards2.items).differences).toEqual([]);
    const missing = compareAwards(f.awards1.awards, []);
    expect(missing.differences.map((d) => d.kind)).toEqual(['only-v1']);
    expect(missing.note).toBe(`per person: ${ANNA} 1`);
    const extra = compareAwards([], [{ id: 'x', badgeId: BADGE, personId: BRAM }]);
    expect(extra.differences.map((d) => d.kind)).toEqual(['only-v2']);
  });

  it('a setting both applications share, but not the ones only v2 prints', () => {
    const f = fixture();
    expect(compareSettings(f.settings1, f.settings2).differences).toEqual([]);
    const changed = structuredClone(f.settings2);
    changed.promoteThreshold = 4;
    changed.intervals = [{ key: 'daily', label: 'Elke dag', periodDays: 1, perCycle: 28 }];
    changed.bonusSchedule[0]!.weekDone = 12;
    expect(compareSettings(f.settings1, changed).differences.map((d) => d.path)).toEqual([
      'bonusSchedule[0].weekDone',
      'intervals[0].label',
      'promoteThreshold',
    ]);
  });

  it('an application that is not healthy', () => {
    const result = compareHealth(
      { status: 'ok', mongo: 'down' },
      { status: 'ok', database: 'ok', version: '1.7.0' },
    );
    expect(result.differences.map((d) => d.key)).toEqual(['v1']);
    expect(result.note).toBe('app-next version 1.7.0');
  });

  it('a side that cannot be read is an error of that check only', async () => {
    const f = fixture();
    const { v1, v2 } = readers(f, {
      v1: { '/rooms': new Error('GET http://node/api/rooms answered 500: boom') },
    });
    const report = await runParity(v1, v2, { range: RANGE });
    expect(report.ok).toBe(false);
    const failed = report.results.filter((r) => r.differences.length > 0);
    expect(failed.map((r) => r.name)).toEqual(['rooms']);
    expect(failed[0]!.differences[0]).toMatchObject({
      kind: 'error',
      message: 'GET http://node/api/rooms answered 500: boom',
    });
  });

  it('a v2 list that is not a page', async () => {
    const reader: Reader = { get: async () => ({ not: 'a page' }) };
    await expect(readAllV2(reader, '/users')).rejects.toThrow('expected { items, nextCursor }');
  });

  it('a v1 body that is not a list', () => {
    expect(() => unwrapV1({ other: [] }, 'badges')).toThrow(
      'expected a list or an object with "badges"',
    );
    expect(unwrapV1({ badges: [1] }, 'badges')).toEqual([1]);
    expect(unwrapV1([2], 'badges')).toEqual([2]);
  });
});

describe('report', () => {
  it('prints at most the given number of differences per check and says how many more', () => {
    const v1 = Array.from({ length: 5 }, (_, i) => ({ _id: String(i), name: 'a' }));
    const v2 = Array.from({ length: 5 }, (_, i) => ({ id: String(i), name: 'b' }));
    const result = compareLists({ name: 'rooms', v1, v2, keyOf: (r) => String(r.id) });
    const text = formatReport({ results: [result], ok: false }, 2);
    expect(text.split('\n').filter((l) => l.startsWith('       - '))).toHaveLength(2);
    expect(text).toContain('... and 3 more');
  });
});

describe('http reader', () => {
  it('only sends GET, with the profile header when given, and parses JSON', async () => {
    const seen: { url: string; init: RequestInit }[] = [];
    const fetchImpl = (async (url: string, init: RequestInit) => {
      seen.push({ url, init });
      return new Response('{"ok":true}', { status: 200 });
    }) as unknown as typeof fetch;
    const reader = httpReader('http://127.0.0.1:3000/api/', { profileId: ANNA, fetchImpl });
    await expect(reader.get('/users')).resolves.toEqual({ ok: true });
    expect(seen).toHaveLength(1);
    expect(seen[0]!.url).toBe('http://127.0.0.1:3000/api/users');
    expect(seen[0]!.init.method).toBe('GET');
    expect(seen[0]!.init.body).toBeUndefined();
    expect(seen[0]!.init.headers).toMatchObject({ 'x-profile-id': ANNA });
  });

  it('turns an error status into an error that names the request and keeps the body short', async () => {
    const fetchImpl = (async () =>
      new Response('x'.repeat(500), { status: 503 })) as unknown as typeof fetch;
    const reader = httpReader('http://app-next/api/v2', { fetchImpl });
    await expect(reader.get('/due')).rejects.toThrow(
      /GET http:\/\/app-next\/api\/v2\/due answered 503: x{200}$/,
    );
  });
});

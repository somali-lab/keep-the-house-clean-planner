import type { FastifyBaseLogger } from 'fastify';
import type { Db } from 'mongodb';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fixedClock } from '../src/clock.ts';
import { computeDueList, summarizeDue } from '../src/domain/due.ts';
import { morningMessage, runMorningNotify } from '../src/domain/notify/morning.ts';
import { createNotifier, NotifyError, type NotifyMessage } from '../src/domain/notify/notifier.ts';
import type { UserDoc } from '../src/data/users.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

const TOKEN = 'tok-very-secret';
const MESSAGE: NotifyMessage = { title: 'Keep the House Clean', body: 'Goedemorgen Zoë!', data: { kind: 'morning', openToday: 2 } };

function fetchOk() {
  return vi.fn(async (_url: string | URL | Request, _init?: RequestInit) => new Response('{}', { status: 200 }));
}

function captured(fetchMock: ReturnType<typeof fetchOk>, call = 0) {
  const [url, init] = fetchMock.mock.calls[call]!;
  return { url: String(url), init: init!, headers: init!.headers as Record<string, string>, body: String(init!.body) };
}

function silentLog() {
  return { info: vi.fn(), error: vi.fn(), warn: vi.fn(), debug: vi.fn() } as unknown as FastifyBaseLogger & {
    error: ReturnType<typeof vi.fn>;
  };
}

describe('notifiers', () => {
  it('is off by default', () => {
    expect(createNotifier({ type: 'none', url: undefined, token: undefined })).toBeNull();
  });

  it('ntfy posts plain text to the topic URL with a bearer token and ASCII headers', async () => {
    const fetchMock = fetchOk();
    const notifier = createNotifier({ type: 'ntfy', url: 'https://ntfy.example/huis', token: TOKEN }, { fetchImpl: fetchMock })!;
    await notifier.send({ ...MESSAGE, title: 'Keep the House Clean · Zoë' });

    const { url, init, headers, body } = captured(fetchMock);
    expect(url).toBe('https://ntfy.example/huis');
    expect(init.method).toBe('POST');
    expect(headers).toMatchObject({ Authorization: `Bearer ${TOKEN}`, Title: 'Keep the House Clean  Zoe', Tags: 'broom' });
    expect(headers['Content-Type']).toMatch(/^text\/plain/);
    expect(body).toBe('Goedemorgen Zoë!');
    expect(init.signal).toBeInstanceOf(AbortSignal);
  });

  it('ntfy sends no Authorization header without a token', async () => {
    const fetchMock = fetchOk();
    await createNotifier({ type: 'ntfy', url: 'https://ntfy.example/huis', token: undefined }, { fetchImpl: fetchMock })!.send(MESSAGE);
    expect(captured(fetchMock).headers).not.toHaveProperty('Authorization');
  });

  it('homeassistant posts JSON with the structured fields', async () => {
    const fetchMock = fetchOk();
    const notifier = createNotifier({ type: 'homeassistant', url: 'http://ha.local:8123/api/webhook/huis', token: TOKEN }, { fetchImpl: fetchMock })!;
    await notifier.send(MESSAGE);

    const { url, headers, body } = captured(fetchMock);
    expect(url).toBe('http://ha.local:8123/api/webhook/huis');
    expect(headers).toMatchObject({ 'Content-Type': 'application/json', Authorization: `Bearer ${TOKEN}` });
    expect(JSON.parse(body)).toEqual({ title: 'Keep the House Clean', message: 'Goedemorgen Zoë!', kind: 'morning', openToday: 2 });
  });

  it.each(['ntfy', 'homeassistant'] as const)('%s rejects on HTTP errors and network failures without leaking URL or token', async (type) => {
    const url = 'https://ntfy.example/secret-topic';
    const failing = createNotifier({ type, url, token: TOKEN }, { fetchImpl: vi.fn(async () => new Response('nope', { status: 500 })) })!;
    const httpError = await failing.send(MESSAGE).catch((e: unknown) => e);
    expect(httpError).toBeInstanceOf(NotifyError);
    expect((httpError as NotifyError).status).toBe(500);

    const offline = createNotifier({ type, url, token: TOKEN }, { fetchImpl: vi.fn(async () => Promise.reject(new TypeError('fetch failed'))) })!;
    const networkError = await offline.send(MESSAGE).catch((e: unknown) => e);
    expect(networkError).toBeInstanceOf(NotifyError);
    expect((networkError as NotifyError).status).toBeNull();

    for (const err of [httpError, networkError] as Error[]) {
      expect(err.message).not.toContain(TOKEN);
      expect(err.message).not.toContain('secret-topic');
    }
  });
});

describe('morningMessage', () => {
  it.each([
    [{ mine: 0, anyone: 0, overdue: 0 }, null],
    [{ mine: 1, anyone: 0, overdue: 0 }, 'Goedemorgen Anna! Vandaag staan er 1 taak voor je klaar.'],
    [{ mine: 3, anyone: 2, overdue: 1 }, 'Goedemorgen Anna! Vandaag staan er 3 taken voor je klaar en 2 taken voor wie dan ook. 1 taak is achterstallig.'],
    [{ mine: 0, anyone: 1, overdue: 0 }, 'Goedemorgen Anna! Vandaag staan er 1 taak klaar voor wie dan ook.'],
    [{ mine: 0, anyone: 0, overdue: 4 }, 'Goedemorgen Anna! Vandaag staat er niets voor je gepland. 4 taken zijn achterstallig.'],
  ])('%o', (counts, expected) => {
    expect(morningMessage({ name: 'Anna', ...counts })).toBe(expected);
  });
});

describe('morning notification job', () => {
  let t: TestApp | undefined;

  afterEach(async () => {
    vi.unstubAllGlobals();
    await t?.close();
    t = undefined;
  });

  /** Wednesday 16 Sep 2026: one task for p1 today, one for anyone today, one for p2 tomorrow. */
  async function setup(env: Record<string, string>): Promise<{ t: TestApp; p1: UserDoc; p2: UserDoc }> {
    const app = await createTestApp({ env });
    t = app;
    const [p1, p2] = await seededUsers(app);
    const room = await seededRoom(app, 'Keuken');
    const headers = asProfile(p1);
    const createTask = async (name: string) => {
      const res = await app.app.inject({
        method: 'POST',
        url: '/api/tasks',
        headers,
        payload: { name, roomId: room._id.toHexString(), intervalKey: 'quarter', durationMinutes: 10 },
      });
      return res.json<{ _id: string }>()._id;
    };
    const aanrecht = await createTask('Aanrecht');
    const oven = await createTask('Oven');
    expect((await app.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers })).statusCode).toBe(200);
    for (const [taskId, date, assigneeId] of [
      [aanrecht, '2026-09-16', p1._id.toHexString()],
      [oven, '2026-09-16', null],
      [aanrecht, '2026-09-17', p2._id.toHexString()],
    ] as const) {
      const res = await app.app.inject({ method: 'POST', url: '/api/occurrences', headers, payload: { taskId, date, assigneeId } });
      expect(res.statusCode, res.body).toBe(201);
    }
    return { t: app, p1, p2 };
  }

  it('sends one message per active user via POST /api/jobs/morning-notify', async () => {
    const fetchMock = fetchOk();
    vi.stubGlobal('fetch', fetchMock);
    const { t: app, p1, p2 } = await setup({ NOTIFY_TYPE: 'homeassistant', NOTIFY_URL: 'http://ha.local/api/webhook/huis' });
    const overdue = summarizeDue((await computeDueList(app.db, app.clock.now())).items).overdue;

    const res = await app.app.inject({ method: 'POST', url: '/api/jobs/morning-notify', headers: asProfile(p1) });
    expect(res.statusCode, res.body).toBe(200);
    expect(res.json()).toEqual({ status: 'done', date: '2026-09-16', sent: 2, failed: 0, quiet: 0 });

    const bodies = fetchMock.mock.calls.map((_, i) => JSON.parse(captured(fetchMock, i).body) as Record<string, unknown>);
    expect(bodies).toEqual([
      expect.objectContaining({ userId: p1._id.toHexString(), openToday: 1, openTodayAnyone: 1, overdue, date: '2026-09-16', kind: 'morning' }),
      expect.objectContaining({ userId: p2._id.toHexString(), openToday: 0, openTodayAnyone: 1, overdue }),
    ]);
    expect(bodies[0]!.message).toMatch(/^Goedemorgen Persoon 1! Vandaag staan er 1 taak voor je klaar en 1 taak voor wie dan ook\./);
  });

  it('never fails hard: delivery errors are logged and counted', async () => {
    const { t: app } = await setup({ NOTIFY_TYPE: 'ntfy', NOTIFY_URL: 'https://ntfy.example/huis' });
    const log = silentLog();
    const notifier = createNotifier(app.config.notify, { fetchImpl: vi.fn(async () => Promise.reject(new TypeError('fetch failed'))) });

    const result = await runMorningNotify({ db: app.db, clock: app.clock, log, notifier });
    expect(result).toEqual({ status: 'done', date: '2026-09-16', sent: 0, failed: 2, quiet: 0 });
    expect(log.error).toHaveBeenCalledTimes(2);
    expect(log.error.mock.calls[0]![1]).toBe('morning notification failed');
  });

  it('never fails hard: a database failure is logged and reported as error', async () => {
    const log = silentLog();
    const brokenDb = {
      collection: () => {
        throw new Error('db down');
      },
    } as unknown as Db;
    const notifier = createNotifier({ type: 'ntfy', url: 'https://ntfy.example/huis', token: undefined }, { fetchImpl: fetchOk() });

    const result = await runMorningNotify({ db: brokenDb, clock: fixedClock('2026-09-16T05:30:00.000Z'), log, notifier });
    expect(result).toEqual({ status: 'error', date: null, sent: 0, failed: 0, quiet: 0 });
    expect(log.error).toHaveBeenCalledOnce();
  });

  it('does nothing when notifications are off', async () => {
    const fetchMock = fetchOk();
    vi.stubGlobal('fetch', fetchMock);
    t = await createTestApp();
    const [p1] = await seededUsers(t);
    const res = await t.app.inject({ method: 'POST', url: '/api/jobs/morning-notify', headers: asProfile(p1) });
    expect(res.json()).toEqual({ status: 'disabled', date: null, sent: 0, failed: 0, quiet: 0 });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('skips users with nothing to report', async () => {
    const fetchMock = fetchOk();
    t = await createTestApp({ env: { NOTIFY_TYPE: 'ntfy', NOTIFY_URL: 'https://ntfy.example/huis' } });
    const result = await runMorningNotify({ db: t.db, clock: t.clock, log: silentLog(), notifier: createNotifier(t.config.notify, { fetchImpl: fetchMock }) });
    expect(result).toMatchObject({ status: 'done', sent: 0, quiet: 2 });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('requires a profile', async () => {
    t = await createTestApp();
    expect((await t.app.inject({ method: 'POST', url: '/api/jobs/morning-notify' })).statusCode).toBe(400);
  });
});

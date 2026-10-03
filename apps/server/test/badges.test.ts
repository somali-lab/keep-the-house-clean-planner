/* eslint-disable no-restricted-syntax -- a few tests damage the awards straight in the database, which no repository does, to prove the reconciliation repairs them */
import type {
  AddExampleBadgesResponse,
  Badge,
  BadgeAwardsResponse,
  BadgeProgressResponse,
  BadgesResponse,
  OccurrenceView,
} from '@huishoudplanner/shared';
import type { LightMyRequestResponse } from 'fastify';
import { ObjectId } from 'mongodb';
import { afterEach, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { COLLECTIONS } from '../src/data/db.ts';
import type { UserDoc } from '../src/data/users.ts';
import { listBadgeAwards } from '../src/data/badges.ts';
import { reconcilePoints } from '../src/domain/points.ts';
import { captureWrites } from './helpers/audit.ts';
import { imageInput, JPEG_BYTES, PNG_BYTES, SVG_BYTES, WEBP_BYTES } from './helpers/badgeImages.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

/**
 * ADR-0014: badges are definitions with a rule; the awards are derived from the same audited executions
 * as the points. Wednesday 16 September 2026 is in the week of Monday 14 September (cycle 0). Every task
 * has an occurrence on each Wednesday (person 1), Thursday (person 2) and Friday (nobody) of four weeks.
 */
const apps: TestApp[] = [];

afterEach(async () => {
  for (const app of apps.splice(0)) await app.close();
});

type Method = 'POST' | 'PUT' | 'PATCH' | 'DELETE';
type Rule = Record<string, unknown>;

interface Fixture {
  t: TestApp;
  p1: UserDoc; // administrator
  p2: UserDoc;
  toilet: string; // 10 minutes
  mop: string; // 20 minutes
  vacuum: string; // 30 minutes
  free: string; // 15 minutes, 0 points
  call: (method: Method, url: string, payload?: Record<string, unknown>, actor?: UserDoc) => Promise<LightMyRequestResponse>;
  occurrence: (date: string, taskId: string) => Promise<string>;
  /** Completes work at an instant: the clock moves first. */
  completeAt: (now: string, id: string, payload?: Record<string, unknown>, actor?: UserDoc) => Promise<LightMyRequestResponse>;
  addBadge: (payload: Record<string, unknown>) => Promise<Badge>;
}

async function fixture(options: { now?: string } = {}): Promise<Fixture> {
  const t = await createTestApp(options.now ? { now: options.now } : {});
  apps.push(t);
  const [p1, p2] = await seededUsers(t);
  const call: Fixture['call'] = (method, url, payload, actor = p1) =>
    t.app.inject({ method, url, headers: asProfile(actor), ...(payload ? { payload } : {}) });
  const roomId = (await seededRoom(t, 'Woonkamer'))._id.toHexString();
  const task = async (name: string, durationMinutes: number, extra: Record<string, unknown> = {}) => {
    const res = await call('POST', '/api/tasks', { name, roomId, intervalKey: '1w', durationMinutes, ...extra });
    expect(res.statusCode, res.body).toBe(201);
    return res.json<{ _id: string }>()._id;
  };
  const toilet = await task('Toilet schoonmaken', 10);
  const mop = await task('Vloer dweilen', 20);
  const vacuum = await task('Stofzuigen', 30);
  const free = await task('Planten water geven', 15, { points: 0 });
  const plan = (await findActivePlan(t.db))!;
  const slots = [toilet, mop, vacuum, free].flatMap((taskId) =>
    [0, 1, 2, 3].flatMap((weekIndex) => [
      { taskId, weekIndex, weekday: 3, assigneeId: p1._id.toHexString() },
      { taskId, weekIndex, weekday: 4, assigneeId: p2._id.toHexString() },
      { taskId, weekIndex, weekday: 5, assigneeId: null },
    ]),
  );
  expect((await call('PUT', `/api/cycle-plans/${plan._id.toHexString()}/slots`, { slots })).statusCode).toBe(200);
  expect((await call('POST', '/api/jobs/nightly')).statusCode).toBe(200);

  const occurrence: Fixture['occurrence'] = async (date, taskId) => {
    const res = await t.app.inject({ method: 'GET', url: `/api/occurrences?from=${date}&to=${date}` });
    const found = res.json<OccurrenceView[]>().find((o) => o.taskId === taskId);
    if (!found) throw new Error(`no occurrence of ${taskId} on ${date}`);
    return found._id;
  };
  const completeAt: Fixture['completeAt'] = (now, id, payload = { action: 'complete' }, actor = p1) => {
    t.clock.set(now);
    return call('PATCH', `/api/occurrences/${id}`, payload, actor);
  };
  const addBadge: Fixture['addBadge'] = async (payload) => {
    const res = await call('POST', '/api/badges', payload);
    expect(res.statusCode, res.body).toBe(201);
    return res.json<Badge>();
  };
  return { t, p1, p2, toilet, mop, vacuum, free, call, occurrence, completeAt, addBadge };
}

const executions = (taskIds: string[], threshold: number): Rule => ({ type: 'executions', taskIds, threshold });
const minutes = (taskIds: string[], threshold: number): Rule => ({ type: 'minutes', taskIds, threshold });

const badgeAudit = (t: TestApp, entity: 'badge' | 'badgeAward' = 'badgeAward') =>
  t.db.collection(COLLECTIONS.auditLog).find({ entity }).sort({ _id: 1 }).toArray();

/** The people who hold a badge, by name, with the moment they earned it. */
async function holders(f: Fixture, badgeId: string): Promise<Record<string, string>> {
  const result: Record<string, string> = {};
  for (const award of await listBadgeAwards(f.t.db, { badgeId: new ObjectId(badgeId) })) {
    result[award.personId.equals(f.p1._id) ? 'p1' : award.personId.equals(f.p2._id) ? 'p2' : 'other'] = award.awardedAt.toISOString();
  }
  return result;
}

describe('badge definitions (admin only, audited)', () => {
  it('lets an administrator create, change and delete a badge, with one audit entry per real change', async () => {
    const f = await fixture();
    const created = await f.call('POST', '/api/badges', {
      name: '  Toiletjuffrouw  ',
      description: 'Het toilet vaak schoongemaakt',
      rule: executions([f.toilet], 10),
      image: imageInput(PNG_BYTES, 'image/png'),
    });
    expect(created.statusCode, created.body).toBe(201);
    const badge = created.json<Badge>();
    expect(badge).toMatchObject({
      name: 'Toiletjuffrouw',
      description: 'Het toilet vaak schoongemaakt',
      active: true,
      exampleKey: null,
      rule: { type: 'executions', taskIds: [f.toilet], threshold: 10 },
      image: { contentType: 'image/png', size: PNG_BYTES.length, url: expect.stringMatching(new RegExp(`^/api/badges/${badge._id}/image\\?v=[0-9a-f]{12}$`)) },
    });
    expect(badge.image).not.toHaveProperty('data');

    const creates = await badgeAudit(f.t, 'badge');
    expect(creates).toHaveLength(1);
    expect(creates[0]).toMatchObject({ action: 'create', source: 'ui', after: { name: 'Toiletjuffrouw', active: true, image: { contentType: 'image/png', size: PNG_BYTES.length } } });
    expect(JSON.stringify(creates[0])).not.toContain(PNG_BYTES.toString('base64'));

    const renamed = await f.call('PATCH', `/api/badges/${badge._id}`, { name: 'Toiletkoningin', rule: executions([f.toilet], 12) });
    expect(renamed.statusCode, renamed.body).toBe(200);
    expect(renamed.json<Badge>()).toMatchObject({ name: 'Toiletkoningin', rule: { threshold: 12 } });
    const updates = (await badgeAudit(f.t, 'badge')).filter((e) => e.action === 'update');
    expect(updates).toHaveLength(1);
    expect(updates[0]).toMatchObject({ before: { name: 'Toiletjuffrouw', rule: { threshold: 10 } }, after: { name: 'Toiletkoningin', rule: { threshold: 12 } } });

    // Equal values are a no-op: nothing is written and nothing is audited.
    const same = await captureWrites(f.t, () => f.call('PATCH', `/api/badges/${badge._id}`, { name: 'Toiletkoningin', rule: executions([f.toilet], 12), active: true }));
    expect(same.result.statusCode).toBe(200);
    expect(same.writes).toEqual([]);
    expect(same.auditInserts).toBe(0);

    const list = (await f.t.app.inject({ method: 'GET', url: '/api/badges' })).json<BadgesResponse>();
    expect(list.badges.map((b) => b.name)).toEqual(['Toiletkoningin']);

    const removed = await f.call('DELETE', `/api/badges/${badge._id}`);
    expect(removed.json()).toEqual({ deleted: true });
    expect((await badgeAudit(f.t, 'badge')).filter((e) => e.action === 'delete')).toHaveLength(1);
    expect((await f.call('DELETE', `/api/badges/${badge._id}`)).statusCode).toBe(404);
    expect((await f.call('PATCH', `/api/badges/${badge._id}`, { name: 'Weg' })).statusCode).toBe(404);
  });

  it('keeps writes for administrators: a member and a request without a profile are refused, reading needs no profile', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Alles', rule: executions([], 5) });
    const body = { name: 'Nieuw', rule: executions([], 1) };
    for (const [method, url, payload] of [
      ['POST', '/api/badges', body],
      ['POST', '/api/badges/examples', { language: 'nl' }],
      ['PATCH', `/api/badges/${badge._id}`, { name: 'Anders' }],
      ['DELETE', `/api/badges/${badge._id}`, undefined],
    ] as const) {
      const member = await f.call(method, url, payload as Record<string, unknown> | undefined, f.p2);
      expect(member.statusCode, `${method} ${url}`).toBe(403);
      expect(member.json()).toMatchObject({ code: 'permission_denied' });
      const anonymous = await f.t.app.inject({ method, url, ...(payload ? { payload } : {}) });
      expect(anonymous.statusCode, `${method} ${url}`).toBe(400);
      expect(anonymous.json()).toMatchObject({ code: 'profile_required' });
    }
    expect((await f.t.app.inject({ method: 'GET', url: '/api/badges' })).statusCode).toBe(200);
    expect((await f.t.app.inject({ method: 'GET', url: '/api/badges/awards' })).statusCode).toBe(200);
    expect((await f.t.app.inject({ method: 'GET', url: `/api/badges/progress?personId=${f.p1._id.toHexString()}` })).statusCode).toBe(200);
    expect(await f.t.db.collection(COLLECTIONS.badges).countDocuments()).toBe(1);
  });

  it.each<[string, Record<string, unknown>, string, string]>([
    ['an empty name', { name: '   ', rule: { type: 'executions', taskIds: [], threshold: 1 } }, 'name', 'too_small'],
    ['a name of 61 characters', { name: 'x'.repeat(61), rule: { type: 'executions', taskIds: [], threshold: 1 } }, 'name', 'too_big'],
    ['a description of 201 characters', { name: 'Naam', description: 'x'.repeat(201), rule: { type: 'executions', taskIds: [], threshold: 1 } }, 'description', 'too_big'],
    ['a threshold of 0', { name: 'Naam', rule: { type: 'executions', taskIds: [], threshold: 0 } }, 'rule.threshold', 'too_small'],
    ['a fractional threshold', { name: 'Naam', rule: { type: 'minutes', taskIds: [], threshold: 1.5 } }, 'rule.threshold', 'invalid_type'],
    ['an unknown rule type', { name: 'Naam', rule: { type: 'streak', threshold: 3 } }, 'rule.type', 'invalid_union'],
  ])('rejects %s', async (_label, payload, field) => {
    const f = await fixture();
    const res = await f.call('POST', '/api/badges', payload);
    expect(res.statusCode).toBe(400);
    expect(res.json<{ code: string; details: { field: string }[] }>()).toMatchObject({ code: 'validation_error', details: [expect.objectContaining({ field })] });
    expect(await f.t.db.collection(COLLECTIONS.badges).countDocuments()).toBe(0);
  });

  it('rejects a rule that names a task that does not exist, and a malformed task id', async () => {
    const f = await fixture();
    const unknown = await f.call('POST', '/api/badges', { name: 'Naam', rule: executions([new ObjectId().toHexString()], 1) });
    expect(unknown.statusCode).toBe(400);
    expect(unknown.json()).toMatchObject({ code: 'validation_error', details: [{ field: 'rule.taskIds', message: 'unknown_task' }] });
    const malformed = await f.call('POST', '/api/badges', { name: 'Naam', rule: executions(['nope'], 1) });
    expect(malformed.statusCode).toBe(400);
  });

  it('stores the tasks of a rule once and in a stable order', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Dubbel', rule: executions([f.mop, f.toilet, f.mop], 3) });
    expect(badge.rule).toEqual({ type: 'executions', taskIds: [f.toilet, f.mop].sort(), threshold: 3 });
  });
});

describe('badge images', () => {
  it('serves the image with its type, a cache header that lasts and an ETag, and answers 304 to a matching ETag', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Met plaatje', rule: executions([], 1), image: imageInput(PNG_BYTES, 'image/png') });
    const res = await f.t.app.inject({ method: 'GET', url: badge.image!.url });
    expect(res.statusCode).toBe(200);
    expect(res.headers['content-type']).toBe('image/png');
    expect(res.headers['cache-control']).toBe('public, max-age=31536000, immutable');
    expect(res.headers['x-content-type-options']).toBe('nosniff');
    expect(res.headers['content-security-policy']).toContain("default-src 'none'");
    expect(res.headers.etag).toBe(`"${badge.image!.hash}"`);
    expect(Buffer.from(res.rawPayload).equals(PNG_BYTES)).toBe(true);

    const cached = await f.t.app.inject({ method: 'GET', url: badge.image!.url, headers: { 'if-none-match': `"${badge.image!.hash}"` } });
    expect(cached.statusCode).toBe(304);
    expect(cached.rawPayload.length).toBe(0);
  });

  it('answers 404 for a badge without an image and for an unknown badge', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Zonder', rule: executions([], 1) });
    expect(badge.image).toBeNull();
    expect((await f.t.app.inject({ method: 'GET', url: `/api/badges/${badge._id}/image` })).statusCode).toBe(404);
    expect((await f.t.app.inject({ method: 'GET', url: `/api/badges/${new ObjectId().toHexString()}/image` })).statusCode).toBe(404);
  });

  it('accepts PNG, JPEG and WebP and an image of exactly 256 KB', async () => {
    const f = await fixture();
    const padded = (head: Buffer, size: number) => Buffer.concat([head, Buffer.alloc(size - head.length)]);
    for (const [bytes, type] of [
      [PNG_BYTES, 'image/png'],
      [JPEG_BYTES, 'image/jpeg'],
      [WEBP_BYTES, 'image/webp'],
      [padded(PNG_BYTES, 256 * 1024), 'image/png'],
    ] as const) {
      const res = await f.call('POST', '/api/badges', { name: `Plaatje ${type}`, rule: executions([], 1), image: imageInput(bytes, type) });
      expect(res.statusCode, `${type} ${bytes.length}`).toBe(201);
      expect(res.json<Badge>().image).toMatchObject({ contentType: type, size: bytes.length });
    }
  });

  it.each<[string, () => Record<string, unknown>, string, string]>([
    ['an image over 256 KB', () => imageInput(Buffer.concat([PNG_BYTES, Buffer.alloc(256 * 1024)]), 'image/png'), 'image.data', 'image_too_large'],
    ['an SVG, which can carry script', () => imageInput(SVG_BYTES, 'image/png'), 'image.data', 'unsupported_image_type'],
    ['text that is not an image', () => imageInput(Buffer.from('hello world, not an image'), 'image/png'), 'image.data', 'unsupported_image_type'],
    ['an image of another type than it says', () => imageInput(PNG_BYTES, 'image/jpeg'), 'image.contentType', 'image_type_mismatch'],
    ['text that is not base64', () => ({ contentType: 'image/png', data: 'not base64!' }), 'image.data', 'invalid_base64'],
  ])('rejects %s', async (_label, image, field, message) => {
    const f = await fixture();
    const res = await f.call('POST', '/api/badges', { name: 'Slecht plaatje', rule: executions([], 1), image: image() });
    expect(res.statusCode).toBe(400);
    expect(res.json()).toMatchObject({ code: 'validation_error', details: [{ field, message }] });
    expect(await f.t.db.collection(COLLECTIONS.badges).countDocuments()).toBe(0);
  });

  it('rejects a content type outside PNG, JPEG and WebP', async () => {
    const f = await fixture();
    const res = await f.call('POST', '/api/badges', { name: 'SVG', rule: executions([], 1), image: { contentType: 'image/svg+xml', data: SVG_BYTES.toString('base64') } });
    expect(res.statusCode).toBe(400);
    expect(res.json<{ details: { field: string }[] }>().details[0]!.field).toBe('image.contentType');
  });

  it('audits an image change by its hash, never by its bytes, and a removal as a change', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Plaatje', rule: executions([], 1), image: imageInput(PNG_BYTES, 'image/png') });
    const before = badge.image!;

    const replaced = await f.call('PATCH', `/api/badges/${badge._id}`, { image: imageInput(JPEG_BYTES, 'image/jpeg') });
    expect(replaced.statusCode, replaced.body).toBe(200);
    const after = replaced.json<Badge>().image!;
    expect(after.hash).not.toBe(before.hash);
    expect(after.url).not.toBe(before.url);
    const update = (await badgeAudit(f.t, 'badge')).find((e) => e.action === 'update')!;
    expect(update.before).toEqual({ image: { contentType: 'image/png', size: PNG_BYTES.length, hash: before.hash } });
    expect(update.after).toEqual({ image: { contentType: 'image/jpeg', size: JPEG_BYTES.length, hash: after.hash } });
    expect(JSON.stringify(update)).not.toContain(JPEG_BYTES.toString('base64'));

    // The same picture again is nothing; removing it is a change.
    const again = await captureWrites(f.t, () => f.call('PATCH', `/api/badges/${badge._id}`, { image: imageInput(JPEG_BYTES, 'image/jpeg') }));
    expect(again.writes).toEqual([]);
    expect(again.auditInserts).toBe(0);
    const removed = await f.call('PATCH', `/api/badges/${badge._id}`, { image: null });
    expect(removed.json<Badge>().image).toBeNull();
    expect((await f.t.app.inject({ method: 'GET', url: `/api/badges/${badge._id}/image` })).statusCode).toBe(404);
    expect((await badgeAudit(f.t, 'badge')).filter((e) => e.action === 'update')).toHaveLength(2);
  });
});

describe('awards from executions', () => {
  it('awards a badge at the moment the threshold was crossed, audits it once, and never again', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Toiletjuffrouw', rule: executions([f.toilet], 2) });
    const first = await f.occurrence('2026-09-16', f.toilet);
    const second = await f.occurrence('2026-09-23', f.toilet);
    const third = await f.occurrence('2026-09-30', f.toilet);

    expect((await f.completeAt('2026-09-16T08:00:00.000Z', first)).statusCode).toBe(200);
    expect(await holders(f, badge._id)).toEqual({});
    expect(await badgeAudit(f.t)).toHaveLength(0);

    const crossing = await captureWrites(f.t, () => f.completeAt('2026-09-17T09:30:00.000Z', second));
    expect(crossing.result.statusCode).toBe(200);
    expect(await holders(f, badge._id)).toEqual({ p1: '2026-09-17T09:30:00.000Z' });
    const audit = await badgeAudit(f.t);
    expect(audit).toHaveLength(1);
    expect(audit[0]).toMatchObject({
      entity: 'badgeAward',
      action: 'create',
      source: 'ui',
      meta: { reason: 'complete' },
      after: { badgeId: new ObjectId(badge._id), personId: f.p1._id, awardedAt: new Date('2026-09-17T09:30:00.000Z') },
    });
    expect((audit[0]!.actorId as ObjectId).equals(f.p1._id)).toBe(true);

    // A third execution only moves the progress: the award keeps the moment it was earned, and nothing more is written.
    const more = await captureWrites(f.t, () => f.completeAt('2026-09-18T09:30:00.000Z', third));
    expect(more.writes.filter((w) => w.collection === COLLECTIONS.badgeAwards)).toEqual([]);
    expect(await badgeAudit(f.t)).toHaveLength(1);
    expect(await holders(f, badge._id)).toEqual({ p1: '2026-09-17T09:30:00.000Z' });

    const awards = (await f.t.app.inject({ method: 'GET', url: '/api/badges/awards' })).json<BadgeAwardsResponse>().awards;
    expect(awards).toEqual([{ _id: expect.any(String), badgeId: badge._id, personId: f.p1._id.toHexString(), awardedAt: '2026-09-17T09:30:00.000Z' }]);
    const ofPerson = (await f.t.app.inject({ method: 'GET', url: `/api/badges/awards?personId=${f.p2._id.toHexString()}` })).json<BadgeAwardsResponse>();
    expect(ofPerson.awards).toEqual([]);
  });

  it('shows the progress of a person, also when the badge is not earned yet', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Toiletjuffrouw', rule: executions([f.toilet], 3) });
    const inactive = await f.addBadge({ name: 'Inactief', rule: executions([], 1), active: false });
    await f.completeAt('2026-09-16T08:00:00.000Z', await f.occurrence('2026-09-16', f.toilet));
    await f.completeAt('2026-09-16T08:05:00.000Z', await f.occurrence('2026-09-16', f.vacuum));
    const progress = (await f.t.app.inject({ method: 'GET', url: `/api/badges/progress?personId=${f.p1._id.toHexString()}` })).json<BadgeProgressResponse>();
    expect(progress).toEqual({ personId: f.p1._id.toHexString(), items: [{ badgeId: badge._id, current: 1, threshold: 3, awardedAt: null }] });
    expect(progress.items.some((item) => item.badgeId === inactive._id)).toBe(false);
    expect((await f.t.app.inject({ method: 'GET', url: '/api/badges/progress' })).statusCode).toBe(400);
  });

  it('adds up executed minutes from the duration the occurrences had', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Dweilkampioen', rule: minutes([f.mop], 60) });
    await f.completeAt('2026-09-16T08:00:00.000Z', await f.occurrence('2026-09-16', f.mop));
    expect(await holders(f, badge._id)).toEqual({});
    // A later change of the task's duration never rewrites what was done: the occurrences keep the 20 minutes they were planned with, so two of them are 40, not 240.
    expect((await f.call('PATCH', `/api/tasks/${f.mop}`, { durationMinutes: 120 })).statusCode).toBe(200);
    await f.completeAt('2026-09-17T08:00:00.000Z', await f.occurrence('2026-09-23', f.mop));
    expect(await holders(f, badge._id)).toEqual({});
    await f.completeAt('2026-09-18T08:00:00.000Z', await f.occurrence('2026-09-30', f.mop));
    expect(await holders(f, badge._id)).toEqual({ p1: '2026-09-18T08:00:00.000Z' });
  });

  it('revokes the award when the work is undone below the threshold, and awards it again at the new moment', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Toiletjuffrouw', rule: executions([f.toilet], 2) });
    const first = await f.occurrence('2026-09-16', f.toilet);
    const second = await f.occurrence('2026-09-23', f.toilet);
    await f.completeAt('2026-09-16T08:00:00.000Z', first);
    await f.completeAt('2026-09-17T08:00:00.000Z', second);
    expect(await holders(f, badge._id)).toEqual({ p1: '2026-09-17T08:00:00.000Z' });

    const undone = await f.completeAt('2026-09-17T09:00:00.000Z', second, { action: 'uncomplete' });
    expect(undone.statusCode, undone.body).toBe(200);
    expect(await holders(f, badge._id)).toEqual({});
    const audit = await badgeAudit(f.t);
    expect(audit.map((e) => [e.action, (e.meta as { reason: string }).reason])).toEqual([
      ['create', 'complete'],
      ['delete', 'uncomplete'],
    ]);

    await f.completeAt('2026-09-19T08:00:00.000Z', second);
    expect(await holders(f, badge._id)).toEqual({ p1: '2026-09-19T08:00:00.000Z' });
  });

  it('revokes and awards for work that earns no points, where the ledger does not say who held it', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Plantenvriend', rule: executions([f.free], 1) });
    const id = await f.occurrence('2026-09-16', f.free);
    await f.completeAt('2026-09-16T08:00:00.000Z', id);
    expect(await holders(f, badge._id)).toEqual({ p1: '2026-09-16T08:00:00.000Z' });
    await f.completeAt('2026-09-16T09:00:00.000Z', id, { action: 'uncomplete' });
    expect(await holders(f, badge._id)).toEqual({});
  });

  it('moves the award with an administrator correction of who did the work, and removes it with a deletion', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Toiletjuffrouw', rule: executions([f.toilet], 1) });
    const id = await f.occurrence('2026-09-16', f.toilet);
    await f.completeAt('2026-09-16T08:00:00.000Z', id);
    expect(await holders(f, badge._id)).toEqual({ p1: '2026-09-16T08:00:00.000Z' });

    const moved = await f.call('PATCH', `/api/occurrences/${id}`, {
      action: 'edit_completion',
      date: '2026-09-16',
      completedAt: '2026-09-16T08:30:00.000Z',
      completedBy: f.p2._id.toHexString(),
    });
    expect(moved.statusCode, moved.body).toBe(200);
    expect(await holders(f, badge._id)).toEqual({ p2: '2026-09-16T08:30:00.000Z' });

    expect((await f.call('DELETE', `/api/occurrences/${id}`)).statusCode).toBe(200);
    expect(await holders(f, badge._id)).toEqual({});
  });

  it('credits the person who did the work: on behalf of the assignee, or after taking it over', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Toiletjuffrouw', rule: executions([f.toilet], 1) });
    const onBehalf = await f.occurrence('2026-09-17', f.toilet); // assigned to person 2
    const takenOver = await f.occurrence('2026-09-24', f.toilet); // assigned to person 2

    // Person 1 checks it off for the assignee: person 2 did it.
    expect((await f.completeAt('2026-09-17T08:00:00.000Z', onBehalf, { action: 'complete', completedBy: f.p2._id.toHexString() }, f.p1)).statusCode).toBe(200);
    expect(await holders(f, badge._id)).toEqual({ p2: '2026-09-17T08:00:00.000Z' });

    // Person 1 takes the work over: person 1 did it, and gets the badge.
    expect((await f.completeAt('2026-09-18T08:00:00.000Z', takenOver, { action: 'complete', takeOver: true }, f.p1)).statusCode).toBe(200);
    expect(await holders(f, badge._id)).toEqual({ p1: '2026-09-18T08:00:00.000Z', p2: '2026-09-17T08:00:00.000Z' });
  });

  it('counts recorded extra work, and a one-off task only for a rule that covers every task', async () => {
    const f = await fixture();
    const allTasks = await f.addBadge({ name: 'Alles', rule: executions([], 2) });
    const toiletOnly = await f.addBadge({ name: 'Toilet', rule: executions([f.toilet], 2) });
    const minutesAll = await f.addBadge({ name: 'Minuten', rule: minutes([], 40) });
    f.t.clock.set('2026-09-16T08:00:00.000Z');

    const oneOff = await f.call('POST', '/api/occurrences/one-off', { name: 'Gordijnen ophangen', durationMinutes: 40, date: '2026-09-16', done: true, requestId: 'badge-one-off-request-0001' });
    expect(oneOff.statusCode, oneOff.body).toBe(201);
    // 40 minutes in one go, but one execution of one task.
    expect(await holders(f, allTasks._id)).toEqual({});
    expect(await holders(f, toiletOnly._id)).toEqual({});
    expect(await holders(f, minutesAll._id)).toEqual({ p1: '2026-09-16T08:00:00.000Z' });

    f.t.clock.set('2026-09-16T08:30:00.000Z');
    const extra = await f.call('POST', '/api/occurrences', { taskId: f.toilet, date: '2026-09-16', done: true, requestId: 'badge-extra-request-00001' });
    expect(extra.statusCode, extra.body).toBe(201);
    expect(await holders(f, allTasks._id)).toEqual({ p1: '2026-09-16T08:30:00.000Z' });
    // Only one toilet execution so far: the one-off task did not count for it.
    expect(await holders(f, toiletOnly._id)).toEqual({});

    f.t.clock.set('2026-09-16T09:00:00.000Z');
    const again = await f.call('POST', '/api/occurrences', { taskId: f.toilet, date: '2026-09-16', done: true, requestId: 'badge-extra-request-00002' });
    expect(again.statusCode, again.body).toBe(201);
    expect(await holders(f, toiletOnly._id)).toEqual({ p1: '2026-09-16T09:00:00.000Z' });
  });

  it('ignores work nobody can be credited for and work that is not done', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Alles', rule: executions([], 1) });
    const open = await f.occurrence('2026-09-16', f.toilet);
    expect((await f.call('PATCH', `/api/occurrences/${open}`, { action: 'skip' })).statusCode).toBe(200);
    expect(await holders(f, badge._id)).toEqual({});
    // Done work that was never credited (old data without a person) earns nothing, like in the points ledger.
    const orphan = await f.occurrence('2026-09-18', f.toilet); // unassigned
    await f.t.db.collection(COLLECTIONS.occurrences).updateOne({ _id: new ObjectId(orphan) }, { $set: { status: 'done', completedBy: null, completedAt: null } });
    await reconcilePoints(f.t.systemCtx(), 'admin');
    expect(await holders(f, badge._id)).toEqual({});
  });
});

describe('awards and the badge definition', () => {
  it('re-evaluates when a rule changes, and revokes when a badge is deactivated or deleted, keeping the moment on reactivation', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Toilet', rule: executions([f.toilet], 5) });
    await f.completeAt('2026-09-16T08:00:00.000Z', await f.occurrence('2026-09-16', f.toilet));
    await f.completeAt('2026-09-17T08:00:00.000Z', await f.occurrence('2026-09-23', f.toilet));
    expect(await holders(f, badge._id)).toEqual({});

    // Lowering the threshold awards from the audited data, at the moment it was crossed, not now.
    f.t.clock.set('2026-10-01T10:00:00.000Z');
    expect((await f.call('PATCH', `/api/badges/${badge._id}`, { rule: executions([f.toilet], 2) })).statusCode).toBe(200);
    expect(await holders(f, badge._id)).toEqual({ p1: '2026-09-17T08:00:00.000Z' });
    const summary = (await badgeAudit(f.t)).at(-1)!;
    expect(summary).toMatchObject({ action: 'recompute', entityId: new ObjectId('000000000000000000000003'), meta: { trigger: 'badge', created: 1, updated: 0, removed: 0, changesTotal: 1 } });

    expect((await f.call('PATCH', `/api/badges/${badge._id}`, { active: false })).statusCode).toBe(200);
    expect(await holders(f, badge._id)).toEqual({});
    expect((await f.t.app.inject({ method: 'GET', url: '/api/badges/awards' })).json<BadgeAwardsResponse>().awards).toEqual([]);
    expect((await f.call('PATCH', `/api/badges/${badge._id}`, { active: true })).statusCode).toBe(200);
    expect(await holders(f, badge._id)).toEqual({ p1: '2026-09-17T08:00:00.000Z' });

    // A change that does not touch the rule writes no award at all.
    const rename = await captureWrites(f.t, () => f.call('PATCH', `/api/badges/${badge._id}`, { name: 'Toiletkoningin' }));
    expect(rename.writes.filter((w) => w.collection === COLLECTIONS.badgeAwards)).toEqual([]);

    expect((await f.call('DELETE', `/api/badges/${badge._id}`)).statusCode).toBe(200);
    expect(await listBadgeAwards(f.t.db)).toEqual([]);
  });

  it('moves the moment of an award when earlier work is added', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Toilet', rule: executions([f.toilet], 2) });
    await f.completeAt('2026-09-17T08:00:00.000Z', await f.occurrence('2026-09-16', f.toilet));
    await f.completeAt('2026-09-18T08:00:00.000Z', await f.occurrence('2026-09-23', f.toilet));
    expect(await holders(f, badge._id)).toEqual({ p1: '2026-09-18T08:00:00.000Z' });
    // An administrator corrects a third completion to a day before the others: the threshold was crossed earlier.
    const late = await f.occurrence('2026-09-30', f.toilet);
    await f.completeAt('2026-09-19T08:00:00.000Z', late);
    await f.call('PATCH', `/api/occurrences/${late}`, { action: 'edit_completion', date: '2026-09-16', completedAt: '2026-09-16T07:00:00.000Z', completedBy: f.p1._id.toHexString() });
    expect(await holders(f, badge._id)).toEqual({ p1: '2026-09-17T08:00:00.000Z' });
    const audit = (await badgeAudit(f.t)).filter((e) => e.action === 'update');
    expect(audit).toHaveLength(1);
    expect(audit[0]).toMatchObject({ before: { awardedAt: new Date('2026-09-18T08:00:00.000Z') }, after: { awardedAt: new Date('2026-09-17T08:00:00.000Z') }, meta: { reason: 'correction' } });
  });
});

describe('recomputation', () => {
  it('is idempotent: running it again changes, writes and audits nothing, and an award exists once per badge and person', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Toilet', rule: executions([f.toilet], 1) });
    await f.completeAt('2026-09-16T08:00:00.000Z', await f.occurrence('2026-09-16', f.toilet));
    expect(Object.keys(await holders(f, badge._id))).toEqual(['p1']);
    const awardsBefore = await listBadgeAwards(f.t.db);
    const auditBefore = (await badgeAudit(f.t)).length;

    for (let run = 0; run < 2; run++) {
      const capture = await captureWrites(f.t, () => f.call('POST', '/api/points/recompute'));
      expect(capture.result.statusCode, capture.result.body).toBe(200);
      expect(capture.writes.filter((w) => w.collection === COLLECTIONS.badgeAwards)).toEqual([]);
    }
    expect(await listBadgeAwards(f.t.db)).toEqual(awardsBefore);
    expect(await badgeAudit(f.t)).toHaveLength(auditBefore);

    // The key is unique: a second award of the same badge to the same person cannot exist.
    const duplicate = f.t.db.collection(COLLECTIONS.badgeAwards).insertOne({ ...awardsBefore[0]!, _id: new ObjectId() });
    await expect(duplicate).rejects.toMatchObject({ code: 11000 });
  });

  it('repairs drift in one summary entry: a lost award comes back at the same moment, an award without data goes', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Toilet', rule: executions([f.toilet], 1) });
    await f.completeAt('2026-09-16T08:00:00.000Z', await f.occurrence('2026-09-16', f.toilet));
    const [award] = await listBadgeAwards(f.t.db);
    await f.t.db.collection(COLLECTIONS.badgeAwards).deleteOne({ _id: award!._id });
    const stray = { ...award!, _id: new ObjectId(), key: `badge:${badge._id}:${f.p2._id.toHexString()}`, personId: f.p2._id };
    await f.t.db.collection(COLLECTIONS.badgeAwards).insertOne(stray);

    const before = (await badgeAudit(f.t)).length;
    expect((await f.call('POST', '/api/points/recompute')).statusCode).toBe(200);
    expect(await holders(f, badge._id)).toEqual({ p1: '2026-09-16T08:00:00.000Z' });
    const summaries = (await badgeAudit(f.t)).slice(before);
    expect(summaries).toHaveLength(1);
    expect(summaries[0]).toMatchObject({
      action: 'recompute',
      meta: {
        trigger: 'admin',
        created: 1,
        removed: 1,
        changesTotal: 2,
      },
    });
    expect((summaries[0]!.meta as { changes: { change: string }[] }).changes.map((c) => c.change).sort()).toEqual(['created', 'removed']);
  });

  it('is part of the nightly run and of the startup reconciliation', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Toilet', rule: executions([f.toilet], 1) });
    await f.completeAt('2026-09-16T08:00:00.000Z', await f.occurrence('2026-09-16', f.toilet));
    await f.t.db.collection(COLLECTIONS.badgeAwards).deleteMany({});
    const { runNightly } = await import('../src/jobs/nightly.ts');
    await runNightly(f.t.systemCtx());
    expect(Object.keys(await holders(f, badge._id))).toEqual(['p1']);
    expect((await badgeAudit(f.t)).at(-1)).toMatchObject({ meta: { trigger: 'nightly' }, source: 'system' });
  });
});

describe('on-time weeks', () => {
  async function weekFixture(withBonuses: boolean) {
    const f = await fixture({ now: '2026-09-14T06:00:00.000Z' });
    if (withBonuses) {
      const res = await f.call('PATCH', '/api/settings', { periodBonuses: { weekDone: 5, weekOnTime: 3, cycleDone: 0, cycleOnTime: 0 } });
      expect(res.statusCode, res.body).toBe(200);
    }
    // Both people do everything planned for them in the week on time; the unassigned work stays open.
    for (const task of [f.toilet, f.mop, f.vacuum, f.free]) {
      await f.completeAt('2026-09-16T07:00:00.000Z', await f.occurrence('2026-09-16', task), { action: 'complete' }, f.p1);
      await f.completeAt('2026-09-17T07:00:00.000Z', await f.occurrence('2026-09-17', task), { action: 'complete' }, f.p2);
    }
    return f;
  }

  it('awards from the on-time week bonuses once the week is final, at the last day of that week', async () => {
    const f = await weekFixture(true);
    const badge = await f.addBadge({ name: 'Alles op tijd', rule: { type: 'onTimeWeeks', threshold: 1 } });
    // The week is still running: nothing is paid yet, so nothing is earned.
    expect(await holders(f, badge._id)).toEqual({});

    f.t.clock.set('2026-09-21T01:00:00.000Z'); // Monday 03:00 local time
    expect((await f.call('POST', '/api/points/recompute')).statusCode).toBe(200);
    const held = await holders(f, badge._id);
    // Both people had planned work and did all of it on time.
    expect(Object.keys(held).sort()).toEqual(['p1', 'p2']);
    // The bonus of the week of 14 September is dated on Sunday 20 September, local midnight.
    expect(held.p1).toBe('2026-09-19T22:00:00.000Z');

    const progress = (await f.t.app.inject({ method: 'GET', url: `/api/badges/progress?personId=${f.p1._id.toHexString()}` })).json<BadgeProgressResponse>();
    expect(progress.items).toEqual([{ badgeId: badge._id, current: 1, threshold: 1, awardedAt: '2026-09-19T22:00:00.000Z' }]);
  });

  it('awards nothing while no bonuses are configured, because no week is ever paid out', async () => {
    const f = await weekFixture(false);
    const badge = await f.addBadge({ name: 'Alles op tijd', rule: { type: 'onTimeWeeks', threshold: 1 } });
    f.t.clock.set('2026-09-21T01:00:00.000Z');
    expect((await f.call('POST', '/api/points/recompute')).statusCode).toBe(200);
    expect(await holders(f, badge._id)).toEqual({});
  });
});

describe('example badges', () => {
  it('creates the three examples once, with the tasks they are about, and again changes nothing', async () => {
    const f = await fixture();
    const first = await f.call('POST', '/api/badges/examples', { language: 'nl' });
    expect(first.statusCode, first.body).toBe(200);
    const result = first.json<AddExampleBadgesResponse>();
    expect(result.skipped).toBe(0);
    expect(result.created.map((b) => [b.name, b.exampleKey, b.active, b.image])).toEqual([
      ['Alles op tijd', 'example:on_time', true, null],
      ['Toiletjuffrouw', 'example:toilet', true, null],
      ['Dweilkampioen', 'example:mop', true, null],
    ]);
    expect(result.created[0]!.rule).toEqual({ type: 'onTimeWeeks', threshold: 4 });
    expect(result.created[1]!.rule).toEqual({ type: 'executions', taskIds: [f.toilet], threshold: 10 });
    expect(result.created[2]!.rule).toEqual({ type: 'minutes', taskIds: [f.mop], threshold: 300 });
    expect((await badgeAudit(f.t, 'badge')).filter((e) => e.action === 'create')).toHaveLength(3);

    const second = await captureWrites(f.t, () => f.call('POST', '/api/badges/examples', { language: 'en' }));
    expect(second.result.json<AddExampleBadgesResponse>()).toEqual({ created: [], skipped: 3 });
    expect(second.writes).toEqual([]);
    expect(second.auditInserts).toBe(0);
    expect(await f.t.db.collection(COLLECTIONS.badges).countDocuments()).toBe(3);
  });

  it('keeps names and thresholds editable and does not bring a renamed or deleted example back as a duplicate', async () => {
    const f = await fixture();
    const created = (await f.call('POST', '/api/badges/examples', { language: 'nl' })).json<AddExampleBadgesResponse>().created;
    const toilet = created.find((b) => b.exampleKey === 'example:toilet')!;
    const renamed = await f.call('PATCH', `/api/badges/${toilet._id}`, { name: 'Toiletkoningin', rule: executions([f.toilet], 2) });
    expect(renamed.json<Badge>()).toMatchObject({ name: 'Toiletkoningin', exampleKey: 'example:toilet', rule: { threshold: 2 } });
    const again = (await f.call('POST', '/api/badges/examples', { language: 'nl' })).json<AddExampleBadgesResponse>();
    expect(again).toEqual({ created: [], skipped: 3 });
    expect((await f.t.app.inject({ method: 'GET', url: '/api/badges' })).json<BadgesResponse>().badges.map((b) => b.name)).toEqual(['Alles op tijd', 'Toiletkoningin', 'Dweilkampioen']);
  });

  it('writes English names on request', async () => {
    const f = await fixture();
    const result = (await f.call('POST', '/api/badges/examples', { language: 'en' })).json<AddExampleBadgesResponse>();
    expect(result.created.map((b) => b.name)).toEqual(['Always on time', 'Toilet Champion', 'Mop Champion']);
    expect((await f.call('POST', '/api/badges/examples', { language: 'fr' })).statusCode).toBe(400);
  });

  it('creates an example about tasks that do not exist inactive, so it cannot silently count every task', async () => {
    const t = await createTestApp();
    apps.push(t);
    const [p1] = await seededUsers(t);
    const res = await t.app.inject({ method: 'POST', url: '/api/badges/examples', headers: asProfile(p1), payload: {} });
    const created = res.json<AddExampleBadgesResponse>().created;
    expect(created.map((b) => [b.exampleKey, b.active])).toEqual([
      ['example:on_time', true],
      ['example:toilet', false],
      ['example:mop', false],
    ]);
    expect(created[1]!.rule).toEqual({ type: 'executions', taskIds: [], threshold: 10 });
  });

  it('awards an example from the data like any other badge', async () => {
    const f = await fixture();
    const created = (await f.call('POST', '/api/badges/examples', { language: 'nl' })).json<AddExampleBadgesResponse>().created;
    const toilet = created.find((b) => b.exampleKey === 'example:toilet')!;
    await f.call('PATCH', `/api/badges/${toilet._id}`, { rule: executions([f.toilet], 1) });
    await f.completeAt('2026-09-16T08:00:00.000Z', await f.occurrence('2026-09-16', f.toilet));
    expect(await holders(f, toilet._id)).toEqual({ p1: '2026-09-16T08:00:00.000Z' });
  });
});

describe('statistics reset', () => {
  it('rebuilds the awards from what remains', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Toilet', rule: executions([f.toilet], 1) });
    await f.completeAt('2026-09-16T08:00:00.000Z', await f.occurrence('2026-09-16', f.toilet));
    expect(Object.keys(await holders(f, badge._id))).toEqual(['p1']);

    // Purging before today keeps the work of today: the award stays and nothing is written.
    f.t.clock.set('2026-09-16T12:00:00.000Z');
    const purge = await captureWrites(f.t, () => f.call('DELETE', '/api/stats?before=2026-09-16'));
    expect(purge.result.statusCode, purge.result.body).toBe(200);
    expect(purge.writes.filter((w) => w.collection === COLLECTIONS.badgeAwards)).toEqual([]);
    expect(Object.keys(await holders(f, badge._id))).toEqual(['p1']);

    // Starting over reopens everything: the badge is lost with the history it was earned from.
    expect((await f.call('DELETE', '/api/stats')).statusCode).toBe(200);
    expect(await holders(f, badge._id)).toEqual({});
    expect((await badgeAudit(f.t)).at(-1)).toMatchObject({ action: 'recompute', meta: { trigger: 'reset', removed: 1 } });
    // The definition stays.
    expect(await f.t.db.collection(COLLECTIONS.badges).countDocuments()).toBe(1);
  });

  it('removes only the awards that depended on the purged days', async () => {
    const f = await fixture();
    const badge = await f.addBadge({ name: 'Toilet', rule: executions([f.toilet], 2) });
    await f.completeAt('2026-09-16T08:00:00.000Z', await f.occurrence('2026-09-16', f.toilet));
    await f.completeAt('2026-09-23T08:00:00.000Z', await f.occurrence('2026-09-23', f.toilet));
    expect(Object.keys(await holders(f, badge._id))).toEqual(['p1']);
    f.t.clock.set('2026-09-23T12:00:00.000Z');
    expect((await f.call('DELETE', '/api/stats?before=2026-09-20')).statusCode).toBe(200);
    expect(await holders(f, badge._id)).toEqual({});
  });
});

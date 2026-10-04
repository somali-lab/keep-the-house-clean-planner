/* eslint-disable no-restricted-syntax -- this test writes the extra members and collections of the .NET application straight into the database, which no repository of the Node server does, to prove that a rollback to Node reads and edits them (docs/PARALLEL-RUN.md) */
import { ObjectId } from 'mongodb';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { COLLECTIONS, ensureIndexes } from '../src/data/db.ts';
import type { ExportFile } from '../src/domain/transfer.ts';
import { expectAudited } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

/**
 * Rollback requirement of the parallel run (plan section 10 step 6): the .NET application writes an integer `version` on users, rooms,
 * tasks, cycle plans, badges and the settings (ADR-0022), `activationVersion` on the settings, a `pointGuards` and a `migrations`
 * collection. The Node server must read, edit and export such a database without any change.
 */
let t: TestApp;
let importer: TestApp;
let adminHeaders: Record<string, string>;
let userId: string;
let roomId: string;
let taskId: string;
let planId: string;
let badgeId: string;

const json = <T>(res: { json: () => unknown }) => res.json() as T;

beforeAll(async () => {
  t = await createTestApp();
  importer = await createTestApp();
  const [admin, member] = await seededUsers(t);
  adminHeaders = asProfile(admin);
  userId = member._id.toHexString();
  roomId = (await seededRoom(t))._id.toHexString();

  const task = await t.app.inject({
    method: 'POST',
    url: '/api/tasks',
    headers: adminHeaders,
    payload: { name: 'Stofzuigen', roomId, intervalKey: '1w', durationMinutes: 20 },
  });
  taskId = json<{ _id: string }>(task)._id;
  const plan = await t.app.inject({
    method: 'POST',
    url: '/api/cycle-plans',
    headers: adminHeaders,
    payload: { name: 'Concept' },
  });
  planId = json<{ _id: string }>(plan)._id;
  const badge = await t.app.inject({
    method: 'POST',
    url: '/api/badges',
    headers: adminHeaders,
    payload: { name: 'Starter', rule: { type: 'executions', taskIds: [], threshold: 2 } },
  });
  badgeId = json<{ _id: string }>(badge)._id;

  // What the .NET application leaves behind.
  for (const name of [
    COLLECTIONS.users,
    COLLECTIONS.rooms,
    COLLECTIONS.tasks,
    COLLECTIONS.cyclePlans,
    COLLECTIONS.badges,
    COLLECTIONS.settings,
  ]) {
    await t.db.collection(name).updateMany({}, { $set: { version: 3 } });
  }
  await t.db.collection(COLLECTIONS.settings).updateMany({}, { $set: { activationVersion: 2 } });
  await t.db.collection('pointGuards').insertOne({ _id: new ObjectId(userId) });
  await t.db.collection('migrations').insertMany([
    {
      _id: new ObjectId(),
      name: '001-drop-legacy-generated-slot-index',
      appliedAt: new Date('2026-10-01T00:00:00Z'),
    },
    {
      _id: new ObjectId(),
      name: '002-backfill-occurrence-room-snapshots',
      appliedAt: new Date('2026-10-01T00:00:00Z'),
    },
  ]);
});

afterAll(async () => {
  await importer.close();
  await t.close();
});

describe('documents written by the .NET application', () => {
  it('start up on them: the indexes are ensured again next to the extra collections', async () => {
    await expect(ensureIndexes(t.db)).resolves.toBeUndefined();
  });

  it('are read by every list and read endpoint', async () => {
    for (const url of [
      '/api/users',
      '/api/rooms',
      '/api/tasks',
      '/api/cycle-plans',
      '/api/cycle-plans/active',
      '/api/badges',
      '/api/settings',
      '/api/due',
    ]) {
      const res = await t.app.inject({ method: 'GET', url });
      expect(res.statusCode, url).toBe(200);
    }
    const users = json<{ _id: string; name: string; role: string }[]>(
      await t.app.inject({ method: 'GET', url: '/api/users' }),
    );
    expect(users.map((u) => u.name)).toEqual(['Persoon 1', 'Persoon 2']);
    const settings = json<{ timezone: string; promoteThreshold: number }>(
      await t.app.inject({ method: 'GET', url: '/api/settings' }),
    );
    expect(settings.timezone).toBe('Europe/Amsterdam');
  });

  it('are edited by every PATCH, and the audit entry holds only what changed, never the version', async () => {
    const edits: {
      url: string;
      payload: Record<string, unknown>;
      entity: 'user' | 'room' | 'task' | 'cyclePlan' | 'badge' | 'settings';
    }[] = [
      { url: `/api/users/${userId}`, payload: { color: '#222222' }, entity: 'user' },
      { url: `/api/rooms/${roomId}`, payload: { name: 'Badkamer boven' }, entity: 'room' },
      { url: `/api/tasks/${taskId}`, payload: { name: 'Stofzuigen trap' }, entity: 'task' },
      { url: `/api/cycle-plans/${planId}`, payload: { name: 'Concept 2' }, entity: 'cyclePlan' },
      { url: `/api/badges/${badgeId}`, payload: { description: 'Twee keer' }, entity: 'badge' },
      { url: '/api/settings', payload: { promoteThreshold: 5 }, entity: 'settings' },
    ];
    for (const edit of edits) {
      const { result, entries } = await expectAudited(
        t,
        () =>
          t.app.inject({
            method: 'PATCH',
            url: edit.url,
            headers: adminHeaders,
            payload: edit.payload,
          }),
        { entity: edit.entity, action: 'update', count: 1 },
      );
      expect(result.statusCode, edit.url).toBe(200);
      expect(JSON.stringify([entries[0]!.before, entries[0]!.after]), edit.url).not.toMatch(
        /version/i,
      );
    }
  });

  it('do not turn their version into a change: an edit that changes nothing writes and audits nothing', async () => {
    const { result } = await expectAudited(
      t,
      () =>
        t.app.inject({
          method: 'PATCH',
          url: `/api/users/${userId}`,
          headers: adminHeaders,
          payload: { color: '#222222' },
        }),
      { entity: 'user', action: 'update', count: 0 },
    );
    expect(result.statusCode).toBe(200);
  });

  it('keep exporting and importing: the file carries no bookkeeping collections and imports into a fresh database', async () => {
    const exported = await t.app.inject({ method: 'GET', url: '/api/export/json' });
    expect(exported.statusCode).toBe(200);
    const file = json<ExportFile>(exported);
    expect(Object.keys(file.collections)).not.toEqual(expect.arrayContaining(['pointGuards']));
    expect(Object.keys(file.collections)).not.toEqual(expect.arrayContaining(['migrations']));

    const [importAdmin] = await seededUsers(importer);
    const imported = await importer.app.inject({
      method: 'POST',
      url: '/api/import/json?mode=replace&confirm=true',
      headers: asProfile(importAdmin),
      payload: file,
    });
    expect(imported.statusCode, imported.body).toBe(200);
  });
});

import type { LightMyRequestResponse } from 'fastify';
import { ObjectId, type Document } from 'mongodb';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { FieldIssue } from '../src/http/errors.ts';
import { readAllCollections, TRANSFER_COLLECTIONS, type TransferDocs } from '../src/data/transfer.ts';
import type { UserDoc } from '../src/data/users.ts';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { importData, parseImport, type ExportFile } from '../src/domain/transfer.ts';
import { captureWrites } from './helpers/audit.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

let source: TestApp;
let exportResponse: LightMyRequestResponse;
let file: ExportFile;
let snapshot: TransferDocs;
const apps: TestApp[] = [];

async function freshApp(options: Parameters<typeof createTestApp>[0] = {}): Promise<TestApp> {
  const app = await createTestApp(options);
  apps.push(app);
  return app;
}

const importUrl = '/api/import/json?mode=replace&confirm=true';
// The import itself, and the rebuild of the ledger it triggers (ADR-0011), are the only new audit entries.
const withoutImports = (entries: Document[]) =>
  entries.filter((e) => e.entity !== 'import' && !(e.entity === 'points' && e.action === 'recompute'));
const EXTRA_KEY = 'transfer-extra-request-key-0001';
const ONE_OFF_KEY = 'transfer-one-off-request-key-0001';

/** A version-1 file predates recordedDone, requestId and points; strip them to get one from a current export. */
function asVersion1(source: ExportFile): ExportFile {
  const legacy = structuredClone(source);
  legacy.schemaVersion = 1 as never;
  for (const doc of legacy.collections.tasks) delete doc.points;
  for (const doc of legacy.collections.occurrences) {
    delete doc.recordedDone;
    delete doc.requestId;
    delete doc.pointsSnapshot;
  }
  return legacy;
}

beforeAll(async () => {
  // Data with ObjectIds, Dates and nested audit before/after values.
  source = await freshApp();
  const [p1] = await seededUsers(source);
  const headers = asProfile(p1);
  const room = await seededRoom(source, 'Keuken');
  const task = await source.app.inject({
    method: 'POST',
    url: '/api/tasks',
    headers,
    payload: { name: 'Aanrecht', roomId: room._id.toHexString(), intervalKey: 'quarter', durationMinutes: 10, defaultAssigneeId: p1._id.toHexString() },
  });
  expect(task.statusCode, task.body).toBe(201);
  // A slot on the test day, so the export holds a generated occurrence next to the ad-hoc ones.
  const plan = (await findActivePlan(source.db))!;
  const slots = await source.app.inject({
    method: 'PUT',
    url: `/api/cycle-plans/${plan._id.toHexString()}/slots`,
    headers,
    payload: { slots: [{ taskId: task.json<{ _id: string }>()._id, weekIndex: 0, weekday: 3, assigneeId: p1._id.toHexString() }] },
  });
  expect(slots.statusCode, slots.body).toBe(200);
  expect((await source.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers })).statusCode).toBe(200);
  const occurrence = await source.app.inject({
    method: 'POST',
    url: '/api/occurrences',
    headers,
    payload: { taskId: task.json<{ _id: string }>()._id, date: '2026-09-16' },
  });
  expect(occurrence.statusCode, occurrence.body).toBe(201);
  const done = await source.app.inject({
    method: 'PATCH',
    url: `/api/occurrences/${occurrence.json<{ _id: string }>()._id}`,
    headers,
    payload: { action: 'complete' },
  });
  expect(done.statusCode, done.body).toBe(200);
  // Recorded work (ADR-0009): an extra execution and a one-off task, both created done with a request key.
  const extra = await source.app.inject({
    method: 'POST',
    url: '/api/occurrences',
    headers,
    payload: { taskId: task.json<{ _id: string }>()._id, date: '2026-09-16', done: true, requestId: EXTRA_KEY },
  });
  expect(extra.statusCode, extra.body).toBe(201);
  const oneOff = await source.app.inject({
    method: 'POST',
    url: '/api/occurrences/one-off',
    headers,
    payload: { name: 'Gordijnen ophangen', roomId: room._id.toHexString(), durationMinutes: 40, date: '2026-09-16', done: true, requestId: ONE_OFF_KEY },
  });
  expect(oneOff.statusCode, oneOff.body).toBe(201);

  exportResponse = await source.app.inject({ method: 'GET', url: '/api/export/json' });
  file = exportResponse.json<ExportFile>();
  snapshot = await readAllCollections(source.db);
});

afterAll(async () => {
  for (const app of apps) await app.close();
});

describe('GET /api/export/json', () => {
  it('exports every collection as extended JSON with a schema version and a dated filename', () => {
    expect(exportResponse.statusCode).toBe(200);
    expect(exportResponse.headers['content-disposition']).toBe('attachment; filename="huishoudplanner-20260916.json"');
    expect(file.schemaVersion).toBe(5);
    expect(file.exportedAt).toBe('2026-09-16T08:00:00.000Z');
    expect(Object.keys(file.collections).sort()).toEqual([...TRANSFER_COLLECTIONS].sort());
    expect(file.collections.users[0]!._id).toEqual({ $oid: expect.stringMatching(/^[0-9a-f]{24}$/) });
    expect(file.collections.occurrences[0]!.date).toEqual({ $date: expect.any(String) });
    for (const name of TRANSFER_COLLECTIONS) expect(file.collections[name]).toHaveLength(snapshot[name].length);
    expect(snapshot.occurrences.some((o) => o.status === 'done')).toBe(true);
  });

  it('includes recorded work, request keys and one-off tasks without a task id', () => {
    const occurrences = file.collections.occurrences;
    expect(occurrences.find((o) => o.requestId === EXTRA_KEY)).toMatchObject({ origin: 'adhoc', recordedDone: true, taskId: { $oid: expect.any(String) } });
    expect(occurrences.find((o) => o.requestId === ONE_OFF_KEY)).toMatchObject({ origin: 'adhoc', recordedDone: true, taskId: null, taskNameSnapshot: 'Gordijnen ophangen' });
    expect(occurrences.some((o) => o.origin === 'generated')).toBe(true);
  });
});

describe('import', () => {
  it('round-trips into an empty database with identical data', async () => {
    const empty = await freshApp({ seed: false });
    await importData(empty.systemCtx(), parseImport(file));

    const after = await readAllCollections(empty.db);
    for (const name of TRANSFER_COLLECTIONS) {
      if (name === 'auditLog') continue;
      expect(after[name], name).toEqual(snapshot[name]);
    }
    expect(withoutImports(after.auditLog)).toEqual(snapshot.auditLog);
    expect(after.auditLog.filter((e) => e.entity === 'import')).toHaveLength(1);
  });

  it('imports a version-1 file, which has no recordedDone or requestId, unchanged', async () => {
    const legacy = asVersion1(file);
    const parsed = parseImport(legacy);
    expect(parsed.schemaVersion).toBe(1);
    const empty = await freshApp({ seed: false });
    await importData(empty.systemCtx(), parsed);

    const after = await readAllCollections(empty.db);
    // The rebuild (ADR-0011) fills in the points of a file that has none: the same values the live paths wrote.
    expect(after.tasks).toEqual(snapshot.tasks);
    expect(after.occurrences).toEqual(
      snapshot.occurrences.map(({ recordedDone: _r, requestId: _k, pointsSnapshot, ...rest }) =>
        rest.status === 'done' ? { ...rest, pointsSnapshot } : rest,
      ),
    );
    expect(after.occurrences.some((o) => 'recordedDone' in o || 'requestId' in o)).toBe(false);
    const audit = after.auditLog.filter((e) => e.entity === 'import');
    expect(audit).toHaveLength(1);
    expect(audit[0]).toMatchObject({ meta: { schemaVersion: 1 } });
  });

  it('replaces all data via the API, keeps the existing audit log and audits the import once', async () => {
    const target = await freshApp();
    const [actor] = await seededUsers(target);
    const auditBefore = (await readAllCollections(target.db)).auditLog;

    const res = await target.app.inject({ method: 'POST', url: importUrl, headers: asProfile(actor), payload: file as unknown as Record<string, unknown> });
    expect(res.statusCode, res.body).toBe(200);
    expect(res.json()).toEqual({
      replaced: {
        settings: 1,
        users: snapshot.users.length,
        rooms: snapshot.rooms.length,
        tasks: snapshot.tasks.length,
        cyclePlans: snapshot.cyclePlans.length,
        cycles: snapshot.cycles.length,
        occurrences: snapshot.occurrences.length,
        pointEntries: 0,
      },
      auditAdded: snapshot.auditLog.length,
      removedPointEntries: 0,
      removedRedemptions: 0,
    });

    const after = await readAllCollections(target.db);
    expect(after.users).toEqual(snapshot.users);
    expect(after.occurrences).toEqual(snapshot.occurrences);
    // The import entry, and one summary of the ledger rebuild (the ledger is not part of the file).
    expect(after.auditLog).toHaveLength(auditBefore.length + snapshot.auditLog.length + 2);
    expect(after.auditLog.filter((e) => e.entity === 'points' && e.action === 'recompute')).toHaveLength(1);
    const imports = after.auditLog.filter((e) => e.entity === 'import');
    expect(imports).toHaveLength(1);
    expect(imports[0]).toMatchObject({ action: 'create', source: 'ui', meta: { mode: 'replace', schemaVersion: 5, exportedAt: file.exportedAt } });
    expect((imports[0]!.actorId as ObjectId).equals(actor._id)).toBe(true);
  });

  it('refuses to import without confirm=true and writes nothing', async () => {
    const target = await freshApp();
    const [actor] = await seededUsers(target);
    const capture = await captureWrites(target, () =>
      target.app.inject({ method: 'POST', url: '/api/import/json?mode=replace', headers: asProfile(actor), payload: file as unknown as Record<string, unknown> }),
    );
    expect(capture.result.statusCode).toBe(400);
    expect(capture.result.json()).toMatchObject({ code: 'confirmation_required' });
    expect(capture.writes).toEqual([]);
    expect(capture.auditInserts).toBe(0);
  });

  it('requires a profile', async () => {
    const target = await freshApp();
    const res = await target.app.inject({ method: 'POST', url: importUrl, payload: file as unknown as Record<string, unknown> });
    expect(res.statusCode).toBe(400);
    expect(res.json()).toMatchObject({ code: 'profile_required' });
  });

  describe('rejects invalid files before touching any data', () => {
    let target: TestApp;
    let actor: UserDoc;

    beforeAll(async () => {
      target = await freshApp();
      [actor] = await seededUsers(target);
    });

    const cases: [string, (f: ExportFile) => void, FieldIssue][] = [
      ['an unknown schema version', (f) => Object.assign(f, { schemaVersion: 6 }), { field: 'schemaVersion', message: expect.any(String) }],
      ['an invalid field value', (f) => Object.assign(f.collections.users[0]!, { color: 'rood' }), { field: 'collections.users.0.color', message: 'invalid_color' }],
      ['a plain string where an ObjectId belongs', (f) => Object.assign(f.collections.tasks[0]!, { roomId: '0123456789abcdef01234567' }), { field: 'collections.tasks.0.roomId', message: 'expected_object_id' }],
      ['a plain string where a date belongs', (f) => Object.assign(f.collections.occurrences[0]!, { date: '2026-09-16T00:00:00.000Z' }), { field: 'collections.occurrences.0.date', message: 'expected_date' }],
      [
        'a duplicate request id',
        (f) => { f.collections.occurrences.find((o) => o.requestId === ONE_OFF_KEY)!.requestId = EXTRA_KEY; },
        { field: expect.stringMatching(/^collections\.occurrences\.\d+\.requestId$/), message: 'duplicate_request_id' },
      ],
      [
        'a duplicate generated slot',
        (f) => {
          const generated = f.collections.occurrences.find((o) => o.origin === 'generated')!;
          f.collections.occurrences.push({ ...generated, _id: { $oid: new ObjectId().toHexString() } });
        },
        { field: expect.stringMatching(/^collections\.occurrences\.\d+\.plannedDate$/), message: 'duplicate_slot' },
      ],
      ['a missing settings document', (f) => Object.assign(f.collections, { settings: [] }), { field: 'collections.settings', message: 'settings_singleton' }],
      ['no active user', (f) => Object.assign(f.collections, { users: [] }), { field: 'collections.users', message: 'no_active_user' }],
    ];

    it('leaves existing data untouched when a duplicate request id is rejected', async () => {
      const before = await readAllCollections(target.db);
      const broken = structuredClone(file);
      broken.collections.occurrences.forEach((o) => { if (o.requestId === ONE_OFF_KEY) o.requestId = EXTRA_KEY; });
      const res = await target.app.inject({ method: 'POST', url: importUrl, headers: asProfile(actor), payload: broken as unknown as Record<string, unknown> });
      expect(res.statusCode).toBe(400);
      expect(await readAllCollections(target.db)).toEqual(before);
    });

    it.each(cases)('%s', async (_label, mutate, issue) => {
      const broken = structuredClone(file);
      mutate(broken);
      const capture = await captureWrites(target, () =>
        target.app.inject({ method: 'POST', url: importUrl, headers: asProfile(actor), payload: broken as unknown as Record<string, unknown> }),
      );
      expect(capture.result.statusCode).toBe(400);
      const body = capture.result.json<{ code: string; details: FieldIssue[] }>();
      expect(body.code).toBe('validation_error');
      expect(body.details).toContainEqual(issue);
      expect(capture.writes).toEqual([]);
      expect(capture.auditInserts).toBe(0);
    });
  });
});

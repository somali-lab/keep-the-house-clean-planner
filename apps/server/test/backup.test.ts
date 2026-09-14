import { execFile } from 'node:child_process';
import { mkdtemp, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { FastifyBaseLogger } from 'fastify';
import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { fixedClock } from '../src/clock.ts';
import { BackupError, runBackup } from '../src/domain/backup.ts';
import { asProfile, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

vi.mock('node:child_process', () => ({ execFile: vi.fn() }));

type ExecCallback = (error: (Error & { code?: number | string }) | null, stdout: string, stderr: string) => void;
const execFileMock = execFile as unknown as Mock<(command: string, args: string[], options: object, callback: ExecCallback) => void>;

const PASSWORD = 's3cret-pass';
const MONGO_URL = `mongodb://backup:${PASSWORD}@mongo:27017/huishoudplanner`;
// 00:30 on Wednesday 16 Sep in Amsterdam, still the 15th in UTC.
const clock = fixedClock('2026-09-15T22:30:00.000Z');
const log = { info: vi.fn(), error: vi.fn() } as unknown as FastifyBaseLogger;

let dir: string;
let t: TestApp | undefined;

const config = () => ({ mongoUrl: MONGO_URL, backupDir: dir, backupRetentionDays: 14, timezone: 'Europe/Amsterdam' });
const succeed = () => execFileMock.mockImplementation((_command, _args, _options, callback) => callback(null, '', ''));
const fail = (error: Error & { code?: number | string }) =>
  execFileMock.mockImplementation((_command, _args, _options, callback) => callback(error, '', 'error output'));

async function touch(...names: string[]) {
  for (const name of names) await writeFile(join(dir, name), '');
}

beforeEach(async () => {
  dir = await mkdtemp(join(tmpdir(), 'huishoudplanner-backup-'));
  execFileMock.mockReset();
});

afterEach(async () => {
  await t?.close();
  t = undefined;
  await rm(dir, { recursive: true, force: true });
});

describe('runBackup', () => {
  it('runs mongodump into a dated gzip archive', async () => {
    succeed();
    const result = await runBackup({ config: config(), clock, log });

    expect(execFileMock).toHaveBeenCalledOnce();
    const [command, args] = execFileMock.mock.calls[0]!;
    expect(command).toBe('mongodump');
    expect(args).toEqual([`--uri=${MONGO_URL}`, `--archive=${join(dir, 'huishoudplanner-20260916.archive.gz')}`, '--gzip']);
    expect(result).toEqual({ archive: 'huishoudplanner-20260916.archive.gz', deleted: [] });
  });

  it('deletes archives older than the retention and nothing else', async () => {
    succeed();
    await touch(
      'huishoudplanner-20260916.archive.gz',
      'huishoudplanner-20260902.archive.gz', // 14 days: kept
      'huishoudplanner-20260901.archive.gz', // 15 days: deleted
      'huishoudplanner-20250101.archive.gz',
      'huishoudplanner-20200101.json',
      'notes.txt',
    );

    const result = await runBackup({ config: config(), clock, log });
    expect(result.deleted).toEqual(['huishoudplanner-20250101.archive.gz', 'huishoudplanner-20260901.archive.gz']);
    expect((await readdir(dir)).sort()).toEqual([
      'huishoudplanner-20200101.json',
      'huishoudplanner-20260902.archive.gz',
      'huishoudplanner-20260916.archive.gz',
      'notes.txt',
    ]);
  });

  it('keeps old archives and hides the URI when mongodump fails', async () => {
    fail(Object.assign(new Error(`Command failed: mongodump --uri=${MONGO_URL}`), { code: 1 }));
    await touch('huishoudplanner-20250101.archive.gz');

    const error = await runBackup({ config: config(), clock, log }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(BackupError);
    expect((error as BackupError).exitCode).toBe(1);
    expect((error as Error).message).not.toContain(PASSWORD);
    expect((error as Error).message).not.toContain('mongodb://');
    expect(await readdir(dir)).toEqual(['huishoudplanner-20250101.archive.gz']);
  });

  it('reports a missing mongodump binary', async () => {
    fail(Object.assign(new Error('spawn mongodump ENOENT'), { code: 'ENOENT' }));
    await expect(runBackup({ config: config(), clock, log })).rejects.toThrow('mongodump could not run (ENOENT)');
  });
});

describe('POST /api/jobs/backup', () => {
  it('backs up on demand', async () => {
    succeed();
    t = await createTestApp({ env: { BACKUP_DIR: dir } });
    const [p1] = await seededUsers(t);
    const res = await t.app.inject({ method: 'POST', url: '/api/jobs/backup', headers: asProfile(p1) });
    expect(res.statusCode, res.body).toBe(200);
    expect(res.json()).toEqual({ archive: 'huishoudplanner-20260916.archive.gz', deleted: [] });
    expect(execFileMock.mock.calls[0]![1][0]).toBe(`--uri=${t.config.mongoUrl}`);
  });

  it('answers 500 backup_failed when mongodump fails', async () => {
    fail(Object.assign(new Error('Command failed'), { code: 2 }));
    t = await createTestApp({ env: { BACKUP_DIR: dir } });
    const [p1] = await seededUsers(t);
    const res = await t.app.inject({ method: 'POST', url: '/api/jobs/backup', headers: asProfile(p1) });
    expect(res.statusCode).toBe(500);
    expect(res.json()).toEqual({ code: 'backup_failed', message: 'mongodump failed with exit code 2' });
  });

  it('requires a profile', async () => {
    t = await createTestApp({ env: { BACKUP_DIR: dir } });
    expect((await t.app.inject({ method: 'POST', url: '/api/jobs/backup' })).statusCode).toBe(400);
    expect(execFileMock).not.toHaveBeenCalled();
  });
});

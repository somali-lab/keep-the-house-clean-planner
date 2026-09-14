import { execFile } from 'node:child_process';
import { mkdir, readdir, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { daysBetween, today } from '@huishoudplanner/shared';
import type { FastifyBaseLogger } from 'fastify';
import type { Clock } from '../clock.ts';
import type { AppConfig } from '../config.ts';

const ARCHIVE_RE = /^huishoudplanner-(\d{4})(\d{2})(\d{2})\.archive\.gz$/;
const MONGODUMP_TIMEOUT_MS = 10 * 60_000;

/** `huishoudplanner-20260916.archive.gz` */
export const archiveName = (dayKey: string) => `huishoudplanner-${dayKey.replaceAll('-', '')}.archive.gz`;

export class BackupError extends Error {
  readonly exitCode: number | null;
  constructor(message: string, exitCode: number | null) {
    super(message);
    this.name = 'BackupError';
    this.exitCode = exitCode;
  }
}

function mongodump(uri: string, archive: string): Promise<void> {
  return new Promise((resolve, reject) => {
    execFile(
      'mongodump',
      [`--uri=${uri}`, `--archive=${archive}`, '--gzip'],
      { timeout: MONGODUMP_TIMEOUT_MS, windowsHide: true },
      (error) => {
        if (!error) return resolve();
        // The original error repeats the command line, including the URI and its password.
        const code = (error as { code?: unknown }).code;
        reject(
          typeof code === 'number'
            ? new BackupError(`mongodump failed with exit code ${code}`, code)
            : new BackupError(`mongodump could not run (${typeof code === 'string' ? code : 'unknown error'})`, null),
        );
      },
    );
  });
}

/** Deletes our archives older than `retentionDays`; other files are left alone. Returns deleted names. */
export async function pruneBackups(dir: string, todayKey: string, retentionDays: number): Promise<string[]> {
  const expired = (await readdir(dir))
    .filter((name) => {
      const match = ARCHIVE_RE.exec(name);
      return match !== null && daysBetween(`${match[1]}-${match[2]}-${match[3]}`, todayKey) > retentionDays;
    })
    .sort();
  for (const name of expired) await rm(join(dir, name), { force: true });
  return expired;
}

export interface BackupDeps {
  config: Pick<AppConfig, 'mongoUrl' | 'backupDir' | 'backupRetentionDays' | 'timezone'>;
  clock: Clock;
  log: FastifyBaseLogger;
}

export interface BackupResult {
  archive: string;
  deleted: string[];
}

/** mongodump to BACKUP_DIR, then retention. Old archives are only pruned after a successful dump. */
export async function runBackup({ config, clock, log }: BackupDeps): Promise<BackupResult> {
  const todayKey = today(config.timezone, clock.now());
  await mkdir(config.backupDir, { recursive: true });
  const archive = archiveName(todayKey);
  await mongodump(config.mongoUrl, join(config.backupDir, archive));
  const deleted = await pruneBackups(config.backupDir, todayKey, config.backupRetentionDays);
  log.info({ archive, deleted }, 'backup completed');
  return { archive, deleted };
}

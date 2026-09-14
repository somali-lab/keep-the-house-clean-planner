import type { AuditAction, AuditEntity, AuditSource } from '@huishoudplanner/shared';
import type { CommandStartedEvent } from 'mongodb';
import { expect } from 'vitest';
import { COLLECTIONS } from '../../src/data/db.ts';
import type { AuditEntryDoc } from '../../src/data/auditLog.ts';
import type { TestApp } from './testApp.ts';

export interface ExpectedAudit {
  entity: AuditEntity;
  action: AuditAction;
  source?: AuditSource;
  /** Exact number of matching entries; default: at least one. */
  count?: number;
}

/**
 * Runs `fn` and asserts that it produced matching audit entries.
 * Returns the function result and the new matching entries (oldest first).
 */
export async function expectAudited<T>(
  t: TestApp,
  fn: () => Promise<T>,
  expected: ExpectedAudit,
): Promise<{ result: T; entries: AuditEntryDoc[] }> {
  const col = t.db.collection<AuditEntryDoc>(COLLECTIONS.auditLog);
  const countBefore = await col.countDocuments();
  const known = new Set((await col.find({}, { projection: { _id: 1 } }).toArray()).map((d) => d._id.toHexString()));

  const result = await fn();

  const countAfter = await col.countDocuments();
  const fresh = (await col.find({}).sort({ at: 1, _id: 1 }).toArray()).filter(
    (d) => !known.has(d._id.toHexString()),
  );
  expect(countAfter - countBefore, 'new audit entries').toBe(fresh.length);

  const entries = fresh.filter(
    (e) =>
      e.entity === expected.entity &&
      e.action === expected.action &&
      (expected.source === undefined || e.source === expected.source),
  );
  const label = `audit ${expected.entity}/${expected.action}${expected.source ? `/${expected.source}` : ''}`;
  if (expected.count === undefined) {
    expect(entries.length, label).toBeGreaterThanOrEqual(1);
  } else {
    expect(entries, label).toHaveLength(expected.count);
  }
  return { result, entries };
}

const WRITE_COMMANDS = new Set(['insert', 'update', 'delete', 'findAndModify']);

export interface CapturedWrite {
  command: string;
  collection: string;
}

export interface WriteCapture<T> {
  result: T;
  writes: CapturedWrite[];
  auditInserts: number;
  /** Writes to non-audit collections happened without any audit insert. */
  unaudited: boolean;
}

/** Records every write command sent to the test database while `fn` runs. */
export async function captureWrites<T>(t: TestApp, fn: () => Promise<T>): Promise<WriteCapture<T>> {
  const events: CapturedWrite[] = [];
  const listener = (event: CommandStartedEvent) => {
    if (event.databaseName !== t.dbName || !WRITE_COMMANDS.has(event.commandName)) return;
    events.push({ command: event.commandName, collection: String(event.command[event.commandName]) });
  };
  t.client.on('commandStarted', listener);
  let result: T;
  try {
    result = await fn();
  } finally {
    t.client.off('commandStarted', listener);
  }
  const writes = events.filter((e) => e.collection !== COLLECTIONS.auditLog);
  const auditInserts = events.filter(
    (e) => e.collection === COLLECTIONS.auditLog && e.command === 'insert',
  ).length;
  return { result, writes, auditInserts, unaudited: writes.length > 0 && auditInserts === 0 };
}

/** Asserts that every write during `fn` is accompanied by at least one audit insert. */
export async function expectWritesAudited<T>(t: TestApp, fn: () => Promise<T>): Promise<WriteCapture<T>> {
  const capture = await captureWrites(t, fn);
  expect(capture.unaudited, `unaudited writes: ${JSON.stringify(capture.writes)}`).toBe(false);
  return capture;
}

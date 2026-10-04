import { describe, expect, it } from 'vitest';
import { auditQuery, bonusPeriodStarts, calendarRanges, toAuditEntry, type AuditEntry } from './auditModel.ts';

const PERSON = 'a00000000000000000000001';

const entry = (overrides: Partial<AuditEntry>): AuditEntry => ({
  id: 'e1',
  at: '2026-09-16T08:00:00.000Z',
  actorId: PERSON,
  entity: 'points',
  entityId: 'l1',
  action: 'recompute',
  before: {},
  after: {},
  source: 'system',
  meta: null,
  ...overrides,
});

describe('toAuditEntry', () => {
  it('keeps the entry and leaves the stored values as they are', () => {
    const raw = {
      id: 'e1',
      at: '2026-09-16T08:00:00.000Z',
      actorId: PERSON,
      entity: 'task',
      entityId: 't1',
      action: 'update',
      before: { durationMinutes: 30 },
      after: { durationMinutes: 45 },
      source: 'ui',
      meta: null,
    };
    expect(toAuditEntry(raw as never)).toEqual(raw);
  });

  it('reads a missing meta as null', () => {
    expect(toAuditEntry({ id: 'e1', at: 'x', actorId: PERSON, entity: 'task', entityId: 't', action: 'create', before: {}, after: {}, source: 'ui' } as never).meta).toBeNull();
  });
});

describe('auditQuery', () => {
  it('leaves out every filter that is empty and always carries the page size', () => {
    expect(auditQuery({}, null)).toEqual({ limit: '50' });
    expect(auditQuery({ entity: '', entityId: '', actorId: '' }, null)).toEqual({ limit: '50' });
  });

  it('names the filters the server knows and the cursor of the next page', () => {
    expect(
      auditQuery(
        { entity: 'task', entityId: 't1', actorId: PERSON, from: '2026-09-01T00:00:00.000Z', to: '2026-09-30T21:59:59.999Z' },
        'c1',
      ),
    ).toEqual({
      entity: 'task',
      entityId: 't1',
      actorId: PERSON,
      from: '2026-09-01T00:00:00.000Z',
      to: '2026-09-30T21:59:59.999Z',
      limit: '50',
      cursor: 'c1',
    });
  });
});

describe('bonusPeriodStarts', () => {
  it('lists the first days of the weeks that week bonuses of reconciliations name, once and in order', () => {
    const recompute = (keys: string[]) =>
      entry({ meta: { bonusChanges: keys.map((key) => ({ key, personId: PERSON, amount: 5, change: 'created' })) } });
    expect(
      bonusPeriodStarts([
        recompute([`bonus_week_done:${PERSON}:2026-09-28`, `bonus_week_ontime:${PERSON}:2026-09-21`]),
        recompute([`bonus_week_done:${PERSON}:2026-09-28`, `bonus_cycle_done:${PERSON}:2026-09-07`, 'broken', `bonus_week_done:${PERSON}:soon`]),
        entry({ entity: 'task', meta: null }),
      ]),
    ).toEqual(['2026-09-21', '2026-09-28']);
  });
});

describe('calendarRanges', () => {
  it('is empty without days', () => {
    expect(calendarRanges([], 371)).toEqual([]);
  });

  it('covers days that lie within the longest range the server answers with one range', () => {
    expect(calendarRanges(['2026-09-28', '2026-09-07', '2026-09-28'], 371)).toEqual([{ from: '2026-09-07', to: '2026-09-28' }]);
  });

  it('starts a new range when the next day would make the range longer than allowed', () => {
    // 2026-09-01 to 2026-09-10 is 10 days
    expect(calendarRanges(['2026-09-01', '2026-09-10', '2026-09-11', '2026-09-20'], 10)).toEqual([
      { from: '2026-09-01', to: '2026-09-10' },
      { from: '2026-09-11', to: '2026-09-20' },
    ]);
  });
});

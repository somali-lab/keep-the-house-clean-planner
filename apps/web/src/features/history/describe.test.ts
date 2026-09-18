import type { AuditEntry } from '@huishoudplanner/shared';
import { describe, expect, it } from 'vitest';
import { describeEntry, formatValue, SYSTEM_ACTOR_ID, type NameLookup } from './describe.ts';

const ANNA = 'a00000000000000000000001';
const BRAM = 'b00000000000000000000002';

const names: NameLookup = {
  users: new Map([
    [ANNA, 'Anna'],
    [BRAM, 'Bram'],
  ]),
  tasks: new Map([['t1', 'Badkamer schoonmaken']]),
  rooms: new Map([
    ['r1', 'Badkamer'],
    ['r2', 'Keuken'],
  ]),
  plans: new Map([['p1', 'Zomerplan']]),
  intervals: new Map([
    ['1w', '1x per week'],
    ['2wk', '1x per 2 weken'],
  ]),
  occurrences: new Map([['o1', 'Wastafel']]),
  timezone: 'Europe/Amsterdam',
};

const entry = (overrides: Partial<AuditEntry>): AuditEntry => ({
  _id: 'e1',
  at: '2026-09-16T08:00:00.000Z',
  actorId: ANNA,
  entity: 'task',
  entityId: 't1',
  action: 'update',
  before: {},
  after: {},
  source: 'ui',
  ...overrides,
});

describe('describeEntry', () => {
  it('shows before → after for a duration change, as in the plan', () => {
    expect(describeEntry(entry({ before: { durationMinutes: 30 }, after: { durationMinutes: 45 } }), names)).toEqual([
      'Anna wijzigde duur van Badkamer schoonmaken: 30 → 45 min',
    ]);
  });

  it('writes one line per changed field and resolves ids to names', () => {
    const lines = describeEntry(
      entry({ before: { name: 'Badkamer', intervalKey: '1w', roomId: 'r1' }, after: { name: 'Badkamer grondig', intervalKey: '2wk', roomId: 'r2' } }),
      names,
    );
    expect(lines).toEqual([
      'Anna wijzigde naam van Badkamer schoonmaken: Badkamer → Badkamer grondig',
      'Anna wijzigde interval van Badkamer schoonmaken: 1x per week → 1x per 2 weken',
      'Anna wijzigde ruimte van Badkamer schoonmaken: Badkamer → Keuken',
    ]);
  });

  it('flattens nested budget changes', () => {
    const lines = describeEntry(
      entry({ entity: 'user', entityId: BRAM, before: { dailyBudgetMinutes: { weekday: 60 } }, after: { dailyBudgetMinutes: { weekday: 45 } } }),
      names,
    );
    expect(lines).toEqual(['Anna wijzigde budget doordeweeks van Bram: 60 → 45 min']);
  });

  it('names the doer when someone else checked it off', () => {
    const complete = entry({ entity: 'occurrence', entityId: 'o1', action: 'complete', meta: { completedBy: BRAM, wasAssignee: false } });
    expect(describeEntry(complete, names)).toEqual(['Anna vinkte Wastafel af, gedaan door Bram']);
    const own = entry({ entity: 'occurrence', entityId: 'o1', action: 'complete', meta: { completedBy: ANNA, wasAssignee: true } });
    expect(describeEntry(own, names)).toEqual(['Anna vinkte Wastafel af']);
  });

  it('includes task, room and day for every occurrence action', () => {
    const occurrence = {
      taskNameSnapshot: 'Douche schoonmaken',
      roomNameSnapshot: 'Badkamer',
      date: '2026-09-18T22:00:00.000Z',
    };
    const contextual = (action: AuditEntry['action'], overrides: Partial<AuditEntry> = {}) =>
      entry({
        entity: 'occurrence',
        entityId: 'not-in-current-page',
        action,
        meta: { occurrence },
        ...overrides,
      });
    const entity = 'Douche schoonmaken in Badkamer op 19-09-2026';

    expect(describeEntry(contextual('complete'), names)).toEqual([`Anna vinkte ${entity} af`]);
    expect(describeEntry(contextual('uncomplete'), names)).toEqual([`Anna maakte het afvinken van ${entity} ongedaan`]);
    expect(describeEntry(contextual('skip'), names)).toEqual([`Anna sloeg ${entity} over`]);
    expect(describeEntry(contextual('assign', { after: { assigneeId: BRAM } }), names)).toEqual([
      `Anna wees ${entity} toe aan Bram`,
    ]);
  });

  it('describes skip reasons, claims, default assignees, activation and deletions', () => {
    expect(describeEntry(entry({ entity: 'occurrence', entityId: 'o1', action: 'skip', after: { status: 'skipped', skipReason: 'ziek' } }), names)).toEqual([
      'Anna sloeg Wastafel over: "ziek"',
    ]);
    expect(describeEntry(entry({ entity: 'occurrence', entityId: 'o1', action: 'assign', after: { assigneeId: ANNA }, meta: { claim: true } }), names)).toEqual([
      'Anna pakte Wastafel op',
    ]);
    expect(describeEntry(entry({ action: 'assign', before: { defaultAssigneeId: null }, after: { defaultAssigneeId: BRAM } }), names)).toEqual([
      'Anna zette de standaard uitvoerder van Badkamer schoonmaken op Bram',
    ]);
    expect(describeEntry(entry({ entity: 'cyclePlan', entityId: 'p1', action: 'activate' }), names)).toEqual(['Anna activeerde plan Zomerplan']);
    expect(
      describeEntry(entry({ actorId: SYSTEM_ACTOR_ID, source: 'system', entity: 'occurrence', entityId: 'o9', action: 'delete', before: { taskNameSnapshot: 'Ramen' } }), names),
    ).toEqual(['Systeem verwijderde Ramen']);
  });

  it('summarises slot changes of a plan', () => {
    const lines = describeEntry(
      entry({ entity: 'cyclePlan', entityId: 'p1', before: { slots: [{}] }, after: { slots: [{}, {}] } }),
      names,
    );
    expect(lines).toEqual(['Anna paste de planning van Zomerplan aan (2 toegevoegd of gewijzigd, 1 verwijderd of gewijzigd)']);
  });

  it('describes creates with the entity type and reschedules with dates', () => {
    expect(
      describeEntry(entry({ actorId: SYSTEM_ACTOR_ID, source: 'system', entity: 'occurrence', entityId: 'o2', action: 'create', after: { taskNameSnapshot: 'Afwas' } }), names),
    ).toEqual(['Systeem maakte taak op een dag Afwas aan']);
    expect(
      describeEntry(
        entry({ entity: 'occurrence', entityId: 'o1', action: 'reschedule', before: { date: '2026-09-07T22:00:00.000Z' }, after: { date: '2026-09-08T22:00:00.000Z' } }),
        names,
      ),
    ).toEqual(['Anna verplaatste Wastafel op 09-09-2026 van 08-09-2026 naar 09-09-2026']);
  });
});

describe('formatValue', () => {
  it('formats booleans, empty values, weekdays and "wie dan ook"', () => {
    expect(formatValue('active', false, names)).toBe('nee');
    expect(formatValue('notes', '', names)).toBe('');
    expect(formatValue('tags', [], names)).toBe('—');
    expect(formatValue('unavailableWeekdays', [2, 6], names)).toBe('dinsdag, zaterdag');
    expect(formatValue('defaultAssigneeId', null, names)).toBe('Wie dan ook');
    expect(formatValue('cycleAnchorDate', '2026-09-14', names)).toBe('14-09-2026');
  });
});

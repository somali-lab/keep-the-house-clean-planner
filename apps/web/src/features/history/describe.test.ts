import type { AuditEntry } from './auditModel.ts';
import { describe, expect, it } from 'vitest';
import { describeEntry, entityName, formatValue, SYSTEM_ACTOR_ID, type NameLookup } from './describe.ts';

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
  badges: new Map([['b1', 'Toiletjuffrouw']]),
  timezone: 'Europe/Amsterdam',
  weekOf: (dayKey) => ({ '2026-09-28': 40 })[dayKey as '2026-09-28'] ?? null,
  cycleDays: 28,
};

const entry = (overrides: Partial<AuditEntry>): AuditEntry => ({
  id: 'e1',
  at: '2026-09-16T08:00:00.000Z',
  actorId: ANNA,
  entity: 'task',
  entityId: 't1',
  action: 'update',
  before: {},
  after: {},
  source: 'ui',
  meta: null,
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

  it('describes the audit of a one-off task (taskId null) from its snapshots, including a retract', () => {
    const oneOff = { taskId: null, taskNameSnapshot: 'Gordijnen ophangen', roomIdSnapshot: null, roomNameSnapshot: null, date: '2026-09-16T22:00:00.000Z' };
    expect(
      describeEntry(entry({ entity: 'occurrence', entityId: 'ox', action: 'create', after: oneOff, meta: { origin: 'adhoc', kind: 'one_off', recordedDone: true, requestId: null } }), names),
    ).toEqual(['Anna maakte taak op een dag Gordijnen ophangen op 17-09-2026 aan']);
    expect(
      describeEntry(entry({ entity: 'occurrence', entityId: 'ox', action: 'delete', before: oneOff, meta: { reason: 'retract' } }), names),
    ).toEqual(['Anna verwijderde Gordijnen ophangen op 17-09-2026']);
    expect(formatValue('taskId', null, names)).toBe('—');
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

describe('describeEntry for the points ledger', () => {
  const points = (overrides: Partial<AuditEntry>) =>
    entry({ entity: 'points', entityId: 'pe1', meta: { occurrenceId: 'o1', reason: 'complete' }, ...overrides });

  it('reads a ledger entry as what the person gained or lost, not who pressed the button', () => {
    expect(
      describeEntry(points({ actorId: ANNA, action: 'create', after: { personId: BRAM, amount: 3, titleSnapshot: 'Stofzuigen' } }), names),
    ).toEqual(['Bram kreeg 3 punten voor Stofzuigen']);
    expect(
      describeEntry(points({ action: 'delete', before: { personId: BRAM, amount: 3, titleSnapshot: 'Stofzuigen' } }), names),
    ).toEqual(['Bram verloor 3 punten voor Stofzuigen']);
  });

  it('shows a correction as before and after, with the person and the week resolved', () => {
    expect(
      describeEntry(points({ action: 'update', before: { personId: ANNA, weekStart: '2026-09-14' }, after: { personId: BRAM, weekStart: '2026-09-21' } }), names),
    ).toEqual([
      'Anna wijzigde persoon van onbekend: Anna → Bram',
      'Anna wijzigde week van onbekend: 14-09-2026 → 21-09-2026',
    ]);
  });

  it('reads a correction that moves the entry as points moving, with the title and amount from the meta', () => {
    const moved = points({
      action: 'update',
      before: { personId: ANNA, weekStart: '2026-09-14' },
      after: { personId: BRAM, weekStart: '2026-09-21' },
      meta: { occurrenceId: 'o1', reason: 'correction', titleSnapshot: 'Stofzuigen', amount: 3 },
    });
    expect(describeEntry(moved, names)).toEqual(['3 punten voor Stofzuigen gingen van Anna naar Bram']);
    expect(entityName(moved, names)).toBe('Stofzuigen');
    // A date-only correction keeps the generic lines but names the execution.
    expect(
      describeEntry(
        points({ action: 'update', before: { date: '2026-09-14' }, after: { date: '2026-09-15' }, meta: { occurrenceId: 'o1', reason: 'correction', titleSnapshot: 'Stofzuigen', amount: 3 } }),
        names,
      )[0],
    ).toContain('Stofzuigen');
  });

  it('reads a redemption as points exchanged, with the note, and says who booked it for someone else', () => {
    const redemption = { kind: 'redemption', amount: -4, note: 'Pizza', personId: BRAM };
    expect(describeEntry(points({ actorId: BRAM, action: 'create', after: redemption, meta: { reason: 'redemption' } }), names)).toEqual([
      'Bram wisselde 4 punten in (Pizza)',
    ]);
    expect(describeEntry(points({ actorId: BRAM, action: 'create', after: { ...redemption, note: null } }), names)).toEqual(['Bram wisselde 4 punten in']);
    expect(describeEntry(points({ actorId: ANNA, action: 'create', after: { ...redemption, note: null } }), names)).toEqual([
      'Anna wisselde 4 punten in voor Bram',
    ]);
  });

  it('reads taking a redemption back as undoing it', () => {
    expect(
      describeEntry(
        points({ actorId: ANNA, action: 'delete', before: { kind: 'redemption', amount: -4, note: 'Pizza', personId: BRAM }, meta: { reason: 'redemption_undone' } }),
        names,
      ),
    ).toEqual(['Anna maakte het inwisselen van 4 punten voor Bram ongedaan']);
  });

  it('labels the conversion settings', () => {
    expect(describeEntry(entry({ entity: 'settings', entityId: 's1', before: { centsPerPoint: 0 }, after: { centsPerPoint: 10, currencyCode: 'USD' } }), names)).toEqual([
      'Anna wijzigde waarde van een punt (centen) van de instellingen: 0 → 10',
      'Anna wijzigde valuta van de instellingen: — → USD',
    ]);
  });

  it('labels the goals of the reward meter', () => {
    expect(
      describeEntry(
        entry({ entity: 'settings', entityId: 's1', before: { rewardGoals: { cyclePoints: 40 } }, after: { rewardGoals: { weekPoints: 12, cyclePoints: null } } }),
        names,
      ),
    ).toEqual([
      'Anna wijzigde cyclusdoel van de beloningsmeter van de instellingen: 40 → —',
      'Anna wijzigde weekdoel van de beloningsmeter van de instellingen: — → 12',
    ]);
  });

  it('describes a recomputation of the ledger', () => {
    expect(describeEntry(entry({ entity: 'points', action: 'recompute', actorId: SYSTEM_ACTOR_ID }), names)).toEqual([
      'Systeem berekende de punten opnieuw',
    ]);
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

describe('describeEntry for bonuses', () => {
  const weekDone = `bonus_week_done:${BRAM}:2026-09-28`;
  const cycleOnTime = `bonus_cycle_ontime:${ANNA}:2026-09-07`;
  const recompute = (meta: Record<string, unknown>) =>
    entry({ actorId: SYSTEM_ACTOR_ID, source: 'system', entity: 'points', entityId: 'ledger', action: 'recompute', meta });

  it('lists who earned or lost which bonus in a reconciliation, per person, kind and period', () => {
    const lines = describeEntry(
      recompute({
        bonusChanges: [
          { key: weekDone, personId: BRAM, amount: 5, change: 'created' },
          { key: cycleOnTime, personId: ANNA, amount: 10, change: 'removed' },
        ],
        bonusChangesTotal: 2,
        bonusChangesTruncated: false,
      }),
      names,
    );
    expect(lines).toEqual([
      'Systeem berekende de punten opnieuw',
      'Bram kreeg 5 punten: Weekbonus: alles gedaan, week 40',
      'Anna verloor 10 punten: Cyclusbonus: alles op tijd, 7 sep – 4 okt',
    ]);
  });

  it('says how many more bonuses a truncated list holds, and still reads an entry without any bonus changes', () => {
    const lines = describeEntry(
      recompute({ bonusChanges: [{ key: weekDone, personId: BRAM, amount: 5, change: 'created' }], bonusChangesTotal: 103, bonusChangesTruncated: true }),
      names,
    );
    expect(lines.at(-1)).toBe('… en nog 102 bonussen');
    expect(describeEntry(recompute({ created: 2 }), names)).toEqual(['Systeem berekende de punten opnieuw']);
  });

  it('shows the key of a bonus whose week the calendar has not answered yet', () => {
    const lines = describeEntry(
      recompute({ bonusChanges: [{ key: weekDone, personId: BRAM, amount: 5, change: 'created' }], bonusChangesTotal: 1 }),
      { ...names, weekOf: () => null },
    );
    expect(lines.at(-1)).toBe(`Bram kreeg 5 punten: ${weekDone}`);
  });

  it('shows a change of the bonus schedule as readable rows, before and after', () => {
    const lines = describeEntry(
      entry({
        entity: 'settings',
        entityId: 's1',
        before: { bonusSchedule: [] },
        after: { bonusSchedule: [{ from: '2026-09-16', weekDone: 5, weekOnTime: 3, cycleDone: 20, cycleOnTime: 10 }] },
      }),
      names,
    );
    expect(lines).toEqual(['Anna wijzigde bonusbedragen van de instellingen: — → vanaf 16-09-2026: week 5/3, cyclus 20/10']);
  });
});

describe('badges (ADR-0014)', () => {
  it('reads an award as a person earning, moving or losing a badge', () => {
    const award = (overrides: Partial<AuditEntry>) => entry({ entity: 'badgeAward', entityId: 'w1', ...overrides });
    expect(describeEntry(award({ action: 'create', after: { badgeId: 'b1', personId: BRAM }, meta: { reason: 'complete' } }), names)).toEqual([
      'Bram heeft de badge Toiletjuffrouw behaald',
    ]);
    expect(describeEntry(award({ action: 'delete', before: { badgeId: 'b1', personId: BRAM }, meta: { reason: 'uncomplete' } }), names)).toEqual([
      'Bram is de badge Toiletjuffrouw kwijt',
    ]);
    expect(
      describeEntry(award({ action: 'update', before: { awardedAt: '2026-09-18T08:00:00.000Z' }, after: { awardedAt: '2026-09-17T08:00:00.000Z' }, meta: { reason: 'correction', badgeId: 'b1', personId: BRAM } }), names),
    ).toEqual(['De behaaldatum van Bram voor de badge Toiletjuffrouw is aangepast naar 17-09-2026, 10:00']);
    expect(entityName(award({ action: 'create', after: { badgeId: 'b1', personId: BRAM } }), names)).toBe('Toiletjuffrouw');
  });

  it('uses the name carried in the entry when the badge no longer exists', () => {
    const award = entry({ entity: 'badgeAward', entityId: 'w1', action: 'delete', before: { badgeId: 'gone', personId: BRAM }, meta: { reason: 'uncomplete', badgeName: 'Oude badge' } });
    expect(describeEntry(award, names)).toEqual(['Bram is de badge Oude badge kwijt']);
    const summary = entry({
      entity: 'badgeAward',
      entityId: '000000000000000000000003',
      action: 'recompute',
      meta: { trigger: 'badge', created: 0, updated: 0, removed: 1, changes: [{ key: 'k', badgeId: 'gone', badgeName: 'Oude badge', personId: BRAM, change: 'removed' }], changesTotal: 1 },
    });
    expect(describeEntry(summary, names)).toContain('Bram is de badge Oude badge kwijt');
  });

  it('lists who earned or lost which badge in a reconciliation, and how many more there are', () => {
    const lines = describeEntry(
      entry({
        entity: 'badgeAward',
        entityId: '000000000000000000000003',
        action: 'recompute',
        actorId: SYSTEM_ACTOR_ID,
        source: 'system',
        meta: {
          trigger: 'nightly',
          created: 1,
          updated: 0,
          removed: 1,
          changes: [
            { key: 'badge:b1:' + BRAM, badgeId: 'b1', personId: BRAM, change: 'created' },
            { key: 'badge:b1:' + ANNA, badgeId: 'b1', personId: ANNA, change: 'removed' },
          ],
          changesTotal: 5,
          changesTruncated: true,
        },
      }),
      names,
    );
    expect(lines).toEqual([
      'Systeem liet de badges opnieuw berekenen: 1 toegekend, 0 aangepast, 1 ingetrokken',
      'Bram heeft de badge Toiletjuffrouw behaald',
      'Anna is de badge Toiletjuffrouw kwijt',
      '… en nog 3 andere wijzigingen van badges',
    ]);
  });

  it('names a badge definition and describes its rule change by field', () => {
    const lines = describeEntry(
      entry({ entity: 'badge', entityId: 'b1', before: { rule: { threshold: 10 } }, after: { rule: { threshold: 2 } } }),
      names,
    );
    expect(lines).toEqual(['Anna wijzigde drempel van Toiletjuffrouw: 10 → 2']);
    expect(describeEntry(entry({ entity: 'badge', entityId: 'b1', action: 'create', after: { name: 'Toiletjuffrouw' } }), names)).toEqual([
      'Anna maakte badge Toiletjuffrouw aan',
    ]);
  });
});

describe('describeEntry for entries this version does not know', () => {
  it('still reads an entity or an action that a newer server adds', () => {
    expect(describeEntry(entry({ entity: 'newThing', entityId: 'x1', action: 'frobnicate' }), names)).toEqual([
      'Anna wijzigde onbekend',
    ]);
  });
});

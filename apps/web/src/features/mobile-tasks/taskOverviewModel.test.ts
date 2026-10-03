import type { OccurrenceView } from '@huishoudplanner/shared';
import { describe, expect, it } from 'vitest';
import { makeOccurrence, makeRoom, makeTask } from '../../test/render.tsx';
import { taskOverviewRows } from './taskOverviewModel.ts';

const LIVING = makeRoom({ _id: 'r1', name: 'Woonkamer', sortOrder: 1 });
const VACUUM = makeTask({ _id: 't1', name: 'Stofzuigen', roomId: LIVING._id });

describe('taskOverviewRows with one-off tasks (taskId null)', () => {
  const oneOff = (id: string, date: string, overrides: Partial<OccurrenceView> = {}) =>
    makeOccurrence({
      _id: id,
      taskId: null,
      taskNameSnapshot: 'Gordijnen ophangen',
      roomIdSnapshot: LIVING._id,
      roomNameSnapshot: LIVING.name,
      origin: 'adhoc',
      date,
      ...overrides,
    });

  it('gives each one-off task its own row keyed oneoff:<occurrenceId>, from its snapshots', () => {
    const rows = taskOverviewRows(
      [
        makeOccurrence({ _id: 'o1', taskId: VACUUM._id, taskNameSnapshot: VACUUM.name, date: '2026-09-16' }),
        oneOff('x1', '2026-09-17'),
        oneOff('x2', '2026-09-17'),
      ],
      [VACUUM],
      [LIVING],
      'Onbekend',
      '2026-09-16',
      '2026-09-14',
    );
    const keys = rows.map((row) => row.key);
    expect(keys).toHaveLength(3);
    expect(keys).toEqual(expect.arrayContaining(['oneoff:x1', 'oneoff:x2']));
    expect(keys.filter((key) => key.startsWith('t1:'))).toHaveLength(1);
    const one = rows.find((row) => row.key === 'oneoff:x1');
    expect(one).toMatchObject({ taskId: null, taskName: 'Gordijnen ophangen', roomId: LIVING._id, roomName: 'Woonkamer', dates: ['2026-09-17'] });
    expect(new Set(rows.map((row) => row.key)).size).toBe(rows.length);
  });

  it('shows the unknown-room label for a one-off task without a room', () => {
    const rows = taskOverviewRows(
      [oneOff('x3', '2026-09-18', { roomIdSnapshot: null, roomNameSnapshot: null })],
      [],
      [LIVING],
      'Onbekend',
      '2026-09-16',
      '2026-09-14',
    );
    expect(rows).toEqual([
      expect.objectContaining({ key: 'oneoff:x3', taskId: null, roomId: null, roomName: 'Onbekend' }),
    ]);
  });
});

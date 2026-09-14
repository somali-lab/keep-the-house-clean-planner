import { describe, expect, it } from 'vitest';
import { makeRoom, makeTask } from '../../test/render.tsx';
import { groupTasksByRoom, UNKNOWN_ROOM_ID } from './groupTasks.ts';

const keuken = makeRoom({ _id: 'r1', name: 'Keuken', sortOrder: 10 });
const badkamer = makeRoom({ _id: 'r2', name: 'Badkamer', sortOrder: 20 });
const zolder = makeRoom({ _id: 'r3', name: 'Zolder', sortOrder: 5, active: false });
const hal = makeRoom({ _id: 'r4', name: 'Hal', sortOrder: 30 });

describe('groupTasksByRoom', () => {
  it('groups by room in sort order with tasks sorted by name', () => {
    const groups = groupTasksByRoom(
      [
        makeTask({ _id: 't1', name: 'Vloer dweilen', roomId: 'r1' }),
        makeTask({ _id: 't2', name: 'Aanrecht', roomId: 'r1' }),
        makeTask({ _id: 't3', name: 'Douche', roomId: 'r2' }),
      ],
      [badkamer, hal, keuken],
    );
    expect(groups.map((g) => [g.room?.name, g.tasks.map((t) => t.name)])).toEqual([
      ['Keuken', ['Aanrecht', 'Vloer dweilen']],
      ['Badkamer', ['Douche']],
      ['Hal', []],
    ]);
  });

  it('shows inactive rooms only when they still have tasks', () => {
    expect(groupTasksByRoom([], [zolder, keuken]).map((g) => g.roomId)).toEqual(['r1']);
    const withTask = groupTasksByRoom([makeTask({ _id: 't1', name: 'Dozen', roomId: 'r3' })], [zolder, keuken]);
    expect(withTask.map((g) => g.roomId)).toEqual(['r3', 'r1']);
  });

  it('collects tasks with an unknown room at the end', () => {
    const groups = groupTasksByRoom([makeTask({ _id: 't1', name: 'Wees', roomId: 'gone' })], [keuken]);
    expect(groups.at(-1)).toMatchObject({ roomId: UNKNOWN_ROOM_ID, room: null });
    expect(groups.at(-1)?.tasks.map((t) => t.name)).toEqual(['Wees']);
  });
});

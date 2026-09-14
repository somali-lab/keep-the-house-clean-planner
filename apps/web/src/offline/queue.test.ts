import { describe, expect, it } from 'vitest';
import { defaultStore, memoryStore } from './queue.ts';

const item = (id: string) => ({
  action: { id, kind: 'complete' as const },
  profileId: 'a00000000000000000000001',
  taskName: `Taak ${id}`,
  queuedAt: '2026-09-16T08:00:00.000Z',
});

describe('memoryStore', () => {
  it('keeps items in order with increasing sequence numbers and removes by sequence', async () => {
    const store = memoryStore();
    await store.add(item('o1'));
    await store.add(item('o2'));
    await store.add(item('o3'));
    expect((await store.all()).map((i) => [i.seq, i.action.id])).toEqual([
      [1, 'o1'],
      [2, 'o2'],
      [3, 'o3'],
    ]);

    await store.remove(2);
    expect((await store.all()).map((i) => i.action.id)).toEqual(['o1', 'o3']);
  });

  it('is used where IndexedDB does not exist', async () => {
    expect(typeof indexedDB).toBe('undefined');
    const store = defaultStore();
    await store.add(item('o1'));
    expect(await store.all()).toHaveLength(1);
  });
});

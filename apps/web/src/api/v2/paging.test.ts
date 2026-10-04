import { describe, expect, it } from 'vitest';
import { collectPages } from './paging.ts';

describe('collectPages', () => {
  it('follows the cursor of every page until the last one and keeps the order', async () => {
    const seen: (string | undefined)[] = [];
    const all = await collectPages(async (cursor) => {
      seen.push(cursor);
      return cursor === 'b' ? { items: [3], nextCursor: null } : cursor === 'a' ? { items: [2], nextCursor: 'b' } : { items: [1], nextCursor: 'a' };
    });
    expect(all).toEqual([1, 2, 3]);
    expect(seen).toEqual([undefined, 'a', 'b']);
  });

  it('stops with an error on a cursor that repeats instead of looping', async () => {
    const error = await collectPages(async () => ({ items: [1], nextCursor: 'same' })).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(Error);
  });
});

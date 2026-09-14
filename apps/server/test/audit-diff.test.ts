import { ObjectId } from 'mongodb';
import { describe, expect, it } from 'vitest';
import { deepEqual, diffFields, isEmptyDiff } from '../src/audit/diff.ts';

describe('diffFields', () => {
  it('returns only changed top-level fields and ignores updatedAt', () => {
    const diff = diffFields(
      { name: 'Badkamer', durationMinutes: 30, active: true, updatedAt: new Date(1) },
      { name: 'Badkamer', durationMinutes: 45, active: true, updatedAt: new Date(2) },
    );
    expect(diff).toEqual({ before: { durationMinutes: 30 }, after: { durationMinutes: 45 } });
  });

  it('diffs nested objects recursively', () => {
    const diff = diffFields(
      { dailyBudgetMinutes: { weekday: 60, weekend: 120 }, aiProvider: { type: 'none' } },
      { dailyBudgetMinutes: { weekday: 90, weekend: 120 }, aiProvider: { type: 'none' } },
    );
    expect(diff).toEqual({
      before: { dailyBudgetMinutes: { weekday: 60 } },
      after: { dailyBudgetMinutes: { weekday: 90 } },
    });
  });

  it('compares arrays as a whole, by value', () => {
    expect(isEmptyDiff(diffFields({ tags: ['a', 'b'] }, { tags: ['a', 'b'] }))).toBe(true);
    expect(diffFields({ tags: ['a', 'b'] }, { tags: ['b', 'a'] })).toEqual({
      before: { tags: ['a', 'b'] },
      after: { tags: ['b', 'a'] },
    });
    expect(isEmptyDiff(diffFields({ slots: [{ w: 1, d: { x: 1 } }] }, { slots: [{ w: 1, d: { x: 1 } }] }))).toBe(
      true,
    );
  });

  it('compares ObjectIds by value', () => {
    const hex = '0123456789abcdef01234567';
    expect(isEmptyDiff(diffFields({ roomId: new ObjectId(hex) }, { roomId: new ObjectId(hex) }))).toBe(true);
    const other = new ObjectId();
    const diff = diffFields({ roomId: new ObjectId(hex) }, { roomId: other });
    expect(diff.before.roomId).toEqual(new ObjectId(hex));
    expect(diff.after.roomId).toBe(other);
    expect(diffFields({ a: null }, { a: new ObjectId(hex) }).before).toEqual({ a: null });
  });

  it('compares Dates by time', () => {
    expect(isEmptyDiff(diffFields({ at: new Date('2026-09-14') }, { at: new Date('2026-09-14') }))).toBe(true);
    expect(diffFields({ at: new Date('2026-09-14') }, { at: new Date('2026-09-15') }).after).toEqual({
      at: new Date('2026-09-15'),
    });
  });

  it('handles added and removed keys and null documents', () => {
    expect(diffFields({ a: 1 }, { b: 2 })).toEqual({ before: { a: 1 }, after: { b: 2 } });
    expect(diffFields(null, { name: 'x', updatedAt: new Date() })).toEqual({ before: {}, after: { name: 'x' } });
  });

  it('supports custom ignore lists', () => {
    const diff = diffFields({}, { _id: 1, createdAt: 2, name: 'x' }, { ignore: ['_id', 'createdAt'] });
    expect(diff).toEqual({ before: {}, after: { name: 'x' } });
  });
});

describe('deepEqual', () => {
  it('distinguishes types', () => {
    expect(deepEqual(1, '1')).toBe(false);
    expect(deepEqual([], {})).toBe(false);
    expect(deepEqual(new Date(0), 0)).toBe(false);
    expect(deepEqual(new ObjectId(), 'x')).toBe(false);
    expect(deepEqual(null, undefined)).toBe(false);
  });
});

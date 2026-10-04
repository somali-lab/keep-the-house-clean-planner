import { describe, expect, it } from 'vitest';
import { ApiRequestError } from '../../api/index.ts';
import { mockApi, problem } from '../../test/fixtures.ts';
import { makeOccurrenceV2 } from '../../test/render.tsx';
import { fetchOccurrences, sendOccurrenceAction } from './api.ts';

describe('fetchOccurrences', () => {
  it('follows the cursor of every bounded page until the last one', async () => {
    const fetchMock = mockApi({
      '/api/v2/occurrences': (_init: RequestInit | undefined, url: string) => {
        const cursor = new URL(url, 'http://localhost').searchParams.get('cursor');
        return cursor === 'next'
          ? { items: [makeOccurrenceV2({ id: 'o3' })], nextCursor: null }
          : { items: [makeOccurrenceV2({ id: 'o1' }), makeOccurrenceV2({ id: 'o2' })], nextCursor: 'next' };
      },
    });

    const all = await fetchOccurrences('2026-09-14', '2026-09-20');

    expect(all.map((o) => o.id)).toEqual(['o1', 'o2', 'o3']);
    expect(fetchMock.mock.calls.map(([url]) => url)).toEqual([
      '/api/v2/occurrences?from=2026-09-14&to=2026-09-20&limit=500',
      '/api/v2/occurrences?from=2026-09-14&to=2026-09-20&limit=500&cursor=next',
    ]);
  });

  it('stops on a cursor that repeats instead of looping', async () => {
    mockApi({ '/api/v2/occurrences': { items: [makeOccurrenceV2({ id: 'o1' })], nextCursor: 'same' } });
    const error = await fetchOccurrences('2026-09-14', '2026-09-20').catch((e: unknown) => e);
    expect(error).toBeInstanceOf(Error);
  });

  it('turns a problem into an ApiRequestError with the code of the problem', async () => {
    mockApi({ '/api/v2/occurrences': () => problem(400, 'from_after_to', 'from must not be after to') });
    const error = await fetchOccurrences('2026-09-20', '2026-09-14').catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiRequestError);
    expect(error).toMatchObject({ status: 400, code: 'from_after_to', message: 'from must not be after to' });
  });
});

describe('sendOccurrenceAction', () => {
  const answer = makeOccurrenceV2({ id: 'o1' });
  const sentTo = (fetchMock: ReturnType<typeof mockApi>) =>
    fetchMock.mock.calls.map(([url, init]) => [init?.method, url, init?.body ? JSON.parse(String(init.body)) : null]);

  it('sends each action to its intent endpoint with a complete body', async () => {
    const fetchMock = mockApi({
      'POST /api/v2/occurrences/o1/complete': answer,
      'POST /api/v2/occurrences/o1/uncomplete': answer,
      'POST /api/v2/occurrences/o1/skip': answer,
      'POST /api/v2/occurrences/o1/assignment': answer,
      'POST /api/v2/occurrences/o1/claim': answer,
    });

    await sendOccurrenceAction({ id: 'o1', kind: 'complete', completedBy: 'u2' });
    await sendOccurrenceAction({ id: 'o1', kind: 'complete', takeOver: true });
    await sendOccurrenceAction({ id: 'o1', kind: 'uncomplete' });
    await sendOccurrenceAction({ id: 'o1', kind: 'skip', reason: 'ziek' });
    await sendOccurrenceAction({ id: 'o1', kind: 'skip' });
    await sendOccurrenceAction({ id: 'o1', kind: 'assign', assigneeId: null });
    await sendOccurrenceAction({ id: 'o1', kind: 'claim' });

    expect(sentTo(fetchMock)).toEqual([
      ['POST', '/api/v2/occurrences/o1/complete', { completedBy: 'u2' }],
      ['POST', '/api/v2/occurrences/o1/complete', { takeOver: true }],
      ['POST', '/api/v2/occurrences/o1/uncomplete', null],
      ['POST', '/api/v2/occurrences/o1/skip', { reason: 'ziek' }],
      ['POST', '/api/v2/occurrences/o1/skip', {}],
      ['POST', '/api/v2/occurrences/o1/assignment', { assigneeId: null }],
      ['POST', '/api/v2/occurrences/o1/claim', null],
    ]);
  });

  it('maps the answer to an occurrence and keeps its warnings', async () => {
    mockApi({
      'POST /api/v2/occurrences/o1/assignment': {
        ...answer,
        warnings: [{ code: 'assignee_unavailable', message: 'x', details: {} }],
      },
    });
    const result = await sendOccurrenceAction({ id: 'o1', kind: 'assign', assigneeId: 'u1' });
    expect(result.data).toMatchObject({ id: 'o1' });
    expect(result.warnings).toHaveLength(1);
  });

  it('reports an occurrence that was claimed first as already_claimed', async () => {
    mockApi({ 'POST /api/v2/occurrences/o1/claim': () => problem(409, 'already_claimed') });
    await expect(sendOccurrenceAction({ id: 'o1', kind: 'claim' })).rejects.toMatchObject({
      status: 409,
      code: 'already_claimed',
    });
  });
});

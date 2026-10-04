import { QueryClient } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { ApiRequestError } from '../client.ts';
import { createV2Client, unwrap } from './client.ts';
import { etagOf, ifMatch, isStaleEntity, removeFromList, replaceInList, StaleEntityError, versionOfEtag } from './concurrency.ts';

function json(status: number, body: unknown, headers: Record<string, string> = {}, type = 'application/json'): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': type, ...headers } });
}

const problemBody = (status: number, code: string) => ({
  type: `urn:huishoudplanner:problem:${code}`,
  title: code,
  status,
  detail: `${code} detail`,
  traceId: 't',
});

describe('If-Match header', () => {
  it('is the strong validator of the version, in double quotes', () => {
    expect(etagOf(7)).toBe('"7"');
    expect(ifMatch({ version: 3 })).toEqual({ 'If-Match': '"3"' });
  });

  it('reads the version out of an ETag and refuses anything else', () => {
    expect(versionOfEtag('"12"')).toBe(12);
    expect(versionOfEtag(' "0" ')).toBe(0);
    for (const odd of [null, undefined, '', '12', 'W/"12"', '"a"', '"-1"', '"1", "2"', '*']) expect(versionOfEtag(odd)).toBeNull();
  });

  // Every entity write of API v2 and the exact header the generated client sends for it.
  const writes: [string, string, string][] = [
    ['PATCH', '/api/v2/users/{id}', '/api/v2/users/e1'],
    ['PUT', '/api/v2/users/{id}/browser-notifications', '/api/v2/users/e1/browser-notifications'],
    ['PATCH', '/api/v2/rooms/{id}', '/api/v2/rooms/e1'],
    ['DELETE', '/api/v2/rooms/{id}', '/api/v2/rooms/e1'],
    ['PATCH', '/api/v2/tasks/{id}', '/api/v2/tasks/e1'],
    ['DELETE', '/api/v2/tasks/{id}', '/api/v2/tasks/e1'],
    ['PATCH', '/api/v2/cycle-plans/{id}', '/api/v2/cycle-plans/e1'],
    ['PUT', '/api/v2/cycle-plans/{id}/slots', '/api/v2/cycle-plans/e1/slots'],
    ['DELETE', '/api/v2/cycle-plans/{id}', '/api/v2/cycle-plans/e1'],
    ['PATCH', '/api/v2/badges/{id}', '/api/v2/badges/e1'],
    ['DELETE', '/api/v2/badges/{id}', '/api/v2/badges/e1'],
    ['PATCH', '/api/v2/settings', '/api/v2/settings'],
  ];

  it.each(writes)('%s %s sends If-Match "<version>" exactly once, next to the profile header', async (method, template, url) => {
    const fetchImpl = vi.fn(async (_request: RequestInfo | URL) => json(200, {}));
    const client = createV2Client({ getProfileId: () => 'abc', fetchImpl });
    const send = client[method as 'PATCH' | 'PUT' | 'DELETE'] as unknown as (path: string, init: unknown) => Promise<unknown>;

    await send(template, { params: { path: { id: 'e1' }, header: ifMatch({ version: 4 }) }, body: method === 'DELETE' ? undefined : {} });

    const request = fetchImpl.mock.calls[0]![0] as Request;
    expect(request.method).toBe(method);
    expect(new URL(request.url).pathname).toBe(url);
    expect(request.headers.get('If-Match')).toBe('"4"');
    expect(request.headers.get('X-Profile-Id')).toBe('abc');
  });

  it('is not sent by an intent action, which is guarded by its own state', async () => {
    const fetchImpl = vi.fn(async (_request: RequestInfo | URL) => json(200, {}));
    const client = createV2Client({ getProfileId: () => 'abc', fetchImpl });
    await client.POST('/api/v2/occurrences/{id}/claim', { params: { path: { id: 'o1' } } });
    expect((fetchImpl.mock.calls[0]![0] as Request).headers.has('If-Match')).toBe(false);
  });
});

describe('unwrap and the preconditions', () => {
  const patchTask = (fetchImpl: () => Promise<Response>) =>
    unwrap(
      createV2Client({ getProfileId: () => 'abc', fetchImpl }).PATCH('/api/v2/tasks/{id}', {
        params: { path: { id: 't1' }, header: ifMatch({ version: 1 }) },
        body: {} as never,
      }),
    );

  it('throws a StaleEntityError for a 412, with the current version of the ETag header', async () => {
    const error = await patchTask(async () =>
      json(412, problemBody(412, 'precondition_failed'), { ETag: '"5"' }, 'application/problem+json'),
    ).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(StaleEntityError);
    expect(error).toBeInstanceOf(ApiRequestError);
    expect(isStaleEntity(error)).toBe(true);
    expect(error).toMatchObject({ status: 412, code: 'precondition_failed', currentVersion: 5 });
  });

  it('knows no current version when the 412 carries no usable ETag', async () => {
    const error = await patchTask(async () => json(412, problemBody(412, 'precondition_failed'), {}, 'application/problem+json')).catch(
      (e: unknown) => e,
    );
    expect(error).toMatchObject({ currentVersion: null });
  });

  it.each([
    [428, 'precondition_required'],
    [400, 'validation_error'],
  ])('leaves a %s on If-Match a plain ApiRequestError: it is a programming error, not a stale edit', async (status, code) => {
    const error = await patchTask(async () => json(status, problemBody(status, code), {}, 'application/problem+json')).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiRequestError);
    expect(isStaleEntity(error)).toBe(false);
    expect(error).toMatchObject({ status, code });
  });

  it('hands the ETag of a successful answer to the caller', async () => {
    const result = await patchTask(async () => json(200, { id: 't1', version: 2 }, { ETag: '"2"' }));
    expect(result.etag).toBe('"2"');
    expect(result.data).toMatchObject({ version: 2 });
  });

  it('has no etag for an answer without one', async () => {
    const result = await patchTask(async () => json(200, {}));
    expect(result.etag).toBeNull();
  });
});

describe('the cached list after a write', () => {
  const items = [
    { id: 'a', name: 'A', version: 1 },
    { id: 'b', name: 'B', version: 1 },
  ];

  it('takes the answered entity, so its version is the stored one without a re-read', () => {
    const queryClient = new QueryClient();
    queryClient.setQueryData(['x'], items);
    replaceInList(queryClient, ['x'], { id: 'b', name: 'B2', version: 2 });
    expect(queryClient.getQueryData(['x'])).toEqual([items[0], { id: 'b', name: 'B2', version: 2 }]);
  });

  it('leaves an uncached list alone', () => {
    const queryClient = new QueryClient();
    replaceInList(queryClient, ['x'], items[0]!);
    removeFromList(queryClient, ['x'], 'a');
    expect(queryClient.getQueryData(['x'])).toBeUndefined();
  });

  it('drops a deleted entity', () => {
    const queryClient = new QueryClient();
    queryClient.setQueryData(['x'], items);
    removeFromList(queryClient, ['x'], 'a');
    expect(queryClient.getQueryData(['x'])).toEqual([items[1]]);
  });
});

describe('the offline queue', () => {
  // The queue replays check-offs after a connection loss. They are intent actions that carry no ETag, so a replay can never apply a stale
  // one silently; entity writes (the ones that need If-Match) are never queued: the queue type only has these actions.
  it('holds intent actions only, and sending one needs no If-Match', async () => {
    const { isQueueable, sendOccurrenceAction } = await import('../../features/today/api.ts');
    const fetchImpl = vi.fn(async (_request: RequestInfo | URL) => json(200, {}));
    const client = createV2Client({ getProfileId: () => 'abc', fetchImpl });

    for (const action of [
      { id: 'o1', kind: 'complete' },
      { id: 'o1', kind: 'uncomplete' },
      { id: 'o1', kind: 'skip', reason: 'ziek' },
    ] as const) {
      expect(isQueueable(action)).toBe(true);
      await sendOccurrenceAction(action, client).catch(() => undefined);
    }

    expect(fetchImpl).toHaveBeenCalledTimes(3);
    for (const [request] of fetchImpl.mock.calls) expect((request as Request).headers.has('If-Match')).toBe(false);
  });
});

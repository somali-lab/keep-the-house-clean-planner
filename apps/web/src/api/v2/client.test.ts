import { describe, expect, it, vi } from 'vitest';
import { ApiRequestError } from '../client.ts';
import { createV2Client, unwrap } from './client.ts';

function json(status: number, body: unknown, type = 'application/json'): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': type } });
}

describe('v2 api client', () => {
  it('sends the profile and client headers on every request', async () => {
    const fetchImpl = vi.fn(async (_request: RequestInfo | URL) => json(200, { items: [], nextCursor: null }));
    const client = createV2Client({ getProfileId: () => 'abc', fetchImpl });

    await client.GET('/api/v2/occurrences', { params: { query: { from: '2026-09-14', to: '2026-09-20' } } });

    const request = (fetchImpl.mock.calls[0]![0] as Request);
    expect(new URL(request.url).pathname).toBe('/api/v2/occurrences');
    expect(new URL(request.url).search).toBe('?from=2026-09-14&to=2026-09-20');
    expect(request.headers.get('X-Profile-Id')).toBe('abc');
    expect(request.headers.get('X-Client')).toBe('web');
  });

  it('omits the profile header without a profile', async () => {
    const fetchImpl = vi.fn(async (_request: RequestInfo | URL) => json(200, {}));
    const client = createV2Client({ getProfileId: () => null, fetchImpl });
    await client.GET('/api/v2/meta/limits');
    expect((fetchImpl.mock.calls[0]![0] as Request).headers.has('X-Profile-Id')).toBe(false);
  });

  it('sends a JSON body for a write', async () => {
    const fetchImpl = vi.fn(async (_request: RequestInfo | URL) => json(200, {}));
    const client = createV2Client({ getProfileId: () => 'abc', fetchImpl });
    await client.POST('/api/v2/occurrences/{id}/skip', { params: { path: { id: 'o1' } }, body: { reason: 'ziek' } });
    const request = (fetchImpl.mock.calls[0]![0] as Request);
    expect(request.method).toBe('POST');
    expect(new URL(request.url).pathname).toBe('/api/v2/occurrences/o1/skip');
    expect(await request.text()).toBe('{"reason":"ziek"}');
  });
});

describe('unwrap', () => {
  it('returns the data, the status and the warnings of the answer', async () => {
    const fetchImpl = async () =>
      json(201, { id: 'o1', warnings: [{ code: 'assignee_unavailable', message: 'x', details: {} }] });
    const client = createV2Client({ getProfileId: () => 'abc', fetchImpl });
    const result = await unwrap(client.POST('/api/v2/occurrences/{id}/claim', { params: { path: { id: 'o1' } } }));
    expect(result.status).toBe(201);
    expect(result.warnings).toEqual([{ code: 'assignee_unavailable', message: 'x', details: {} }]);
    expect(result.data).toMatchObject({ id: 'o1' });
  });

  it('throws an ApiRequestError that carries the code of the problem type', async () => {
    const fetchImpl = async () =>
      json(
        409,
        { type: 'urn:huishoudplanner:problem:already_claimed', title: 'Conflict', status: 409, detail: 'Taken.', traceId: 't' },
        'application/problem+json',
      );
    const client = createV2Client({ getProfileId: () => 'abc', fetchImpl });
    const error = await unwrap(
      client.POST('/api/v2/occurrences/{id}/claim', { params: { path: { id: 'o1' } } }),
    ).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiRequestError);
    expect(error).toMatchObject({ status: 409, code: 'already_claimed', message: 'Taken.' });
  });

  it('keeps the errors of a validation problem as details', async () => {
    const fetchImpl = async () =>
      json(
        400,
        { type: 'urn:huishoudplanner:problem:validation_error', status: 400, errors: { date: ['required'] } },
        'application/problem+json',
      );
    const client = createV2Client({ getProfileId: () => 'abc', fetchImpl });
    const error = await unwrap(client.GET('/api/v2/calendar', { params: { query: { from: '', to: '' } } })).catch(
      (e: unknown) => e,
    );
    expect(error).toMatchObject({ status: 400, code: 'validation_error', details: { date: ['required'] } });
  });

  it('falls back to http_error for an answer that is not a problem', async () => {
    const fetchImpl = async () => new Response('Bad gateway', { status: 502, statusText: 'Bad Gateway' });
    const client = createV2Client({ getProfileId: () => 'abc', fetchImpl });
    const error = await unwrap(client.GET('/api/v2/meta/limits')).catch((e: unknown) => e);
    expect(error).toMatchObject({ status: 502, code: 'http_error' });
  });

  it('lets a network failure through unchanged', async () => {
    const fetchImpl = async () => {
      throw new TypeError('Failed to fetch');
    };
    const client = createV2Client({ getProfileId: () => 'abc', fetchImpl });
    const error = await unwrap(client.GET('/api/v2/meta/limits')).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(TypeError);
  });
});

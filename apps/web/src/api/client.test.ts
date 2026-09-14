import { describe, expect, it, vi } from 'vitest';
import { ApiRequestError, createApiClient } from './client.ts';

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

describe('api client', () => {
  it('sends profile and client headers and passes warnings through', async () => {
    const fetchImpl = vi.fn(async () =>
      jsonResponse(200, { ok: true, warnings: [{ code: 'over_budget', message: 'x' }] }),
    );
    const client = createApiClient({ getProfileId: () => 'abc', fetchImpl });
    const result = await client.patch('/api/x', { a: 1 });

    const [url, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/x');
    expect(init.headers).toMatchObject({ 'X-Profile-Id': 'abc', 'X-Client': 'web' });
    expect(init.body).toBe('{"a":1}');
    expect(result.warnings).toEqual([{ code: 'over_budget', message: 'x' }]);
  });

  it('omits the profile header without a profile', async () => {
    const fetchImpl = vi.fn(async () => jsonResponse(200, []));
    const client = createApiClient({ getProfileId: () => null, fetchImpl });
    const result = await client.get('/api/users');
    const [, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect(init.headers).not.toHaveProperty('X-Profile-Id');
    expect(result.warnings).toEqual([]);
  });

  it('throws ApiRequestError with code and details', async () => {
    const fetchImpl = vi.fn(async () =>
      jsonResponse(422, { code: 'invalid_plan', message: 'nope', details: [1] }),
    );
    const client = createApiClient({ getProfileId: () => 'abc', fetchImpl });
    const error = await client.put('/api/x', {}).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiRequestError);
    expect(error).toMatchObject({ status: 422, code: 'invalid_plan', details: [1] });
  });
});

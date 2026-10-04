import createClient, { type Middleware } from 'openapi-fetch';
import { ApiRequestError } from '../client.ts';
import type { components, paths } from './schema';

export type ApiWarning = components['schemas']['OccurrenceWarningResponse'];

const PROBLEM_TYPE_PREFIX = 'urn:huishoudplanner:problem:';

export interface V2Result<T> {
  data: T;
  /** The HTTP status; 200 where 201 was expected means the server replayed an earlier request. */
  status: number;
  warnings: ApiWarning[];
}

export interface V2ClientOptions {
  baseUrl?: string;
  /** Active profile id; sent as X-Profile-Id on every request. */
  getProfileId: () => string | null;
  fetchImpl?: typeof fetch;
}

/** The typed client for `/api/v2`, generated from `apps/api/openapi/v2.json` (`npm run generate:api`). */
export function createV2Client(options: V2ClientOptions) {
  const doFetch = options.fetchImpl ?? ((...args: Parameters<typeof fetch>) => fetch(...args));
  // openapi-fetch builds absolute Request objects, so a same-origin client needs the origin of the page.
  const baseUrl = options.baseUrl ?? (typeof location === 'undefined' ? '' : location.origin);
  const client = createClient<paths>({ baseUrl, fetch: doFetch });
  const profileHeaders: Middleware = {
    onRequest({ request }) {
      request.headers.set('X-Client', 'web');
      const profileId = options.getProfileId();
      if (profileId) request.headers.set('X-Profile-Id', profileId);
      return request;
    },
  };
  client.use(profileHeaders);
  return client;
}

export type ApiV2Client = ReturnType<typeof createV2Client>;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** An RFC 9457 problem (`type` is `urn:huishoudplanner:problem:<code>`) as the error the app already handles. */
function problemToError(status: number, statusText: string, problem: unknown): ApiRequestError {
  if (!isRecord(problem)) return new ApiRequestError(status, 'http_error', statusText);
  const type = typeof problem.type === 'string' ? problem.type : '';
  const code = type.startsWith(PROBLEM_TYPE_PREFIX) ? type.slice(PROBLEM_TYPE_PREFIX.length) : 'http_error';
  const message =
    typeof problem.detail === 'string' && problem.detail
      ? problem.detail
      : typeof problem.title === 'string' && problem.title
        ? problem.title
        : statusText;
  const { type: _type, title: _title, status: _status, detail: _detail, instance: _instance, traceId: _traceId, ...extensions } =
    problem;
  const details = 'errors' in extensions ? extensions.errors : Object.keys(extensions).length > 0 ? extensions : undefined;
  return new ApiRequestError(status, code, message, details);
}

type Settled<T> = { data?: T; error?: unknown; response: Response };

/**
 * Turns an openapi-fetch answer into data, or throws an `ApiRequestError` for a problem; a network failure
 * (no answer at all) is not wrapped, so the offline queue can tell it apart.
 */
export async function unwrap<T>(pending: Promise<Settled<T>>): Promise<V2Result<T>> {
  const { data, error, response } = await pending;
  if (!response.ok) throw problemToError(response.status, response.statusText, error);
  const warnings = isRecord(data) && Array.isArray(data.warnings) ? (data.warnings as ApiWarning[]) : [];
  return { data: data as T, status: response.status, warnings };
}

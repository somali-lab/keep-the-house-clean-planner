import type { z } from 'zod';

export class HttpError extends Error {
  readonly statusCode: number;
  readonly code: string;
  readonly details: unknown;
  /** Extra top-level fields for the response body (e.g. `{ weeks }`). */
  readonly extra: Record<string, unknown> | undefined;

  constructor(
    statusCode: number,
    code: string,
    message?: string,
    details?: unknown,
    extra?: Record<string, unknown>,
  ) {
    super(message ?? code);
    this.name = 'HttpError';
    this.statusCode = statusCode;
    this.code = code;
    this.details = details;
    this.extra = extra;
  }
}

export interface FieldIssue {
  field: string;
  message: string;
}

export function zodIssues(error: z.ZodError): FieldIssue[] {
  return error.issues.map((i) => ({ field: i.path.join('.'), message: i.message }));
}

/** Parses input or throws a 400 `validation_error` naming the offending fields. */
export function parseOrThrow<S extends z.ZodType>(schema: S, input: unknown): z.output<S> {
  const result = schema.safeParse(input);
  if (!result.success) {
    throw new HttpError(400, 'validation_error', 'Invalid request', zodIssues(result.error));
  }
  return result.data;
}

export const notFound = (entity: string) => new HttpError(404, 'not_found', `${entity} not found`);

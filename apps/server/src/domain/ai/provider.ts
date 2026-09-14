import type { AiProviderType } from '@huishoudplanner/shared';
import { HttpError } from '../../http/errors.ts';

/** What the AI features ask a provider for: one JSON document as text. */
export interface CompleteJsonRequest {
  /** Stable name of the requested output, e.g. 'plan-proposal'. */
  name: string;
  system: string;
  user: string;
  /** JSON schema of the expected output (also embedded in the prompt). */
  schema: Record<string, unknown>;
}

export interface AiProvider {
  readonly type: AiProviderType;
  completeJson(request: CompleteJsonRequest): Promise<string>;
}

/** AI is switched off in settings (type 'none'). */
export class AiDisabledError extends HttpError {
  constructor() {
    super(503, 'ai_disabled', 'The AI assistant is disabled');
    this.name = 'AiDisabledError';
  }
}

/** Settings or environment are incomplete (e.g. AI_API_KEY missing). Never contains secrets. */
export class AiConfigError extends HttpError {
  constructor(message: string) {
    super(503, 'ai_misconfigured', message);
    this.name = 'AiConfigError';
  }
}

/** The provider failed or answered without usable content. Never echoes the response body or key. */
export class AiProviderError extends HttpError {
  constructor(message: string) {
    super(502, 'ai_provider_error', message);
    this.name = 'AiProviderError';
  }
}

export const DEFAULT_TIMEOUT_MS = 60_000;

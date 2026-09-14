import { AiDisabledError, type AiProvider, type CompleteJsonRequest } from '../provider.ts';

/** AI switched off: every request fails with 503 `ai_disabled`; the rest of the app is unaffected. */
export class NoneProvider implements AiProvider {
  readonly type = 'none' as const;

  async completeJson(_request: CompleteJsonRequest): Promise<string> {
    throw new AiDisabledError();
  }
}

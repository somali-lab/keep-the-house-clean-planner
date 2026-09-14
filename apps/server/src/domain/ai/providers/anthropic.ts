import Anthropic from '@anthropic-ai/sdk';
import { AiProviderError, DEFAULT_TIMEOUT_MS, type AiProvider, type CompleteJsonRequest } from '../provider.ts';

/** Used when settings name no model. */
export const DEFAULT_ANTHROPIC_MODEL = 'claude-opus-5';
export const ANTHROPIC_MAX_TOKENS = 8192;

export interface AnthropicCreateParams {
  model: string;
  max_tokens: number;
  system: string;
  messages: { role: 'user'; content: string }[];
}

/** The one SDK call we use; injectable so tests never reach the network. */
export interface AnthropicMessagesClient {
  messages: {
    create(params: AnthropicCreateParams): Promise<{ content: ReadonlyArray<{ type: string; text?: unknown }> }>;
  };
}

export interface AnthropicOptions {
  /** Only needed without an injected client. Kept inside the SDK client, never on this object. */
  apiKey?: string;
  model: string;
  client?: AnthropicMessagesClient;
  timeoutMs?: number;
}

export class AnthropicProvider implements AiProvider {
  readonly type = 'anthropic' as const;
  private readonly model: string;
  private readonly create: AnthropicMessagesClient['messages']['create'];

  constructor(options: AnthropicOptions) {
    this.model = options.model;
    if (options.client) {
      const client = options.client;
      this.create = (params) => client.messages.create(params);
    } else {
      const sdk = new Anthropic({
        apiKey: options.apiKey,
        maxRetries: 1,
        timeout: options.timeoutMs ?? DEFAULT_TIMEOUT_MS,
      });
      this.create = (params) => sdk.messages.create(params);
    }
  }

  async completeJson(request: CompleteJsonRequest): Promise<string> {
    let message: Awaited<ReturnType<AnthropicMessagesClient['messages']['create']>>;
    try {
      message = await this.create({
        model: this.model,
        max_tokens: ANTHROPIC_MAX_TOKENS,
        system: request.system,
        messages: [{ role: 'user', content: request.user }],
      });
    } catch (err) {
      // Only the status is reported: SDK messages may contain request details.
      const status = typeof (err as { status?: unknown }).status === 'number' ? (err as { status: number }).status : null;
      throw new AiProviderError(status ? `Anthropic API returned HTTP ${status}` : 'Anthropic API could not be reached');
    }

    const text = message.content
      .filter((block) => block.type === 'text' && typeof block.text === 'string')
      .map((block) => block.text as string)
      .join('')
      .trim();
    if (!text) throw new AiProviderError('Anthropic API returned no content');
    return text;
  }
}

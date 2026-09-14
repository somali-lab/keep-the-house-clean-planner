import { DEFAULT_AI_TIMEOUT_SECONDS, type AiProviderSettings } from '@huishoudplanner/shared';
import { defaultMockResponders } from './mockResponders.ts';
import { AiConfigError, type AiProvider } from './provider.ts';
import { AnthropicProvider, DEFAULT_ANTHROPIC_MODEL, type AnthropicMessagesClient } from './providers/anthropic.ts';
import { MockProvider, type MockResponders } from './providers/mock.ts';
import { NoneProvider } from './providers/none.ts';
import { DEFAULT_OLLAMA_ENDPOINT, OllamaProvider } from './providers/ollama.ts';
import { OpenAiCompatibleProvider } from './providers/openaiCompatible.ts';

export interface AiFactoryOptions {
  /** Secret from AI_API_KEY; never stored in settings, never logged. */
  apiKey?: string;
  fetchImpl?: typeof fetch;
  mockResponders?: MockResponders;
  mockInvalidFirst?: boolean;
  anthropicClient?: AnthropicMessagesClient;
}

/** Picks the provider from `settings.aiProvider`; configuration problems become 503 `ai_misconfigured`. */
export function createAiProvider(settings: AiProviderSettings, options: AiFactoryOptions = {}): AiProvider {
  switch (settings.type) {
    case 'none':
      return new NoneProvider();
    case 'mock':
      return new MockProvider({
        responders: options.mockResponders ?? defaultMockResponders,
        invalidFirst: options.mockInvalidFirst,
      });
    case 'anthropic':
      if (!options.apiKey && !options.anthropicClient) throw new AiConfigError('AI_API_KEY is not set');
      return new AnthropicProvider({
        apiKey: options.apiKey,
        model: settings.model ?? DEFAULT_ANTHROPIC_MODEL,
        client: options.anthropicClient,
      });
    case 'openai-compatible':
      if (!settings.endpoint || !settings.model) {
        throw new AiConfigError('An endpoint and model are required for an OpenAI-compatible provider');
      }
      return new OpenAiCompatibleProvider({
        endpoint: settings.endpoint,
        model: settings.model,
        apiKey: options.apiKey,
        fetchImpl: options.fetchImpl,
      });
    case 'ollama':
      if (!settings.model) throw new AiConfigError('A model is required for Ollama');
      return new OllamaProvider({
        endpoint: settings.endpoint ?? DEFAULT_OLLAMA_ENDPOINT,
        model: settings.model,
        timeoutMs: (settings.timeoutSeconds ?? DEFAULT_AI_TIMEOUT_SECONDS) * 1000,
        fetchImpl: options.fetchImpl,
      });
  }
}

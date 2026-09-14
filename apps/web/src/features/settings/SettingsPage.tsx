import {
  DEFAULT_AI_TIMEOUT_SECONDS,
  MAX_AI_TIMEOUT_SECONDS,
  MIN_AI_TIMEOUT_SECONDS,
  type AiProviderSettings,
  type AiProviderType,
  type Settings,
} from '@huishoudplanner/shared';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Bot, Save, TestTube2 } from 'lucide-react';
import { useId, useState, type FormEvent } from 'react';
import { NativeSelect } from '@/components/NativeSelect';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { api, ApiRequestError } from '../../api/index.ts';
import { queryKeys, useSettings } from '../../api/queries.ts';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import { CalendarSection } from './CalendarSection.tsx';
import { DataSection } from './DataSection.tsx';
import { RoomsSection } from './RoomsSection.tsx';
import { Field, FormActions, FormMessage, SettingsCardHeader, settingsCardClass } from './SettingsCard.tsx';
import { UsersSection } from './UsersSection.tsx';

const PROVIDERS: AiProviderType[] = ['none', 'mock', 'anthropic', 'openai-compatible', 'ollama'];
const NEEDS_ENDPOINT: AiProviderType[] = ['openai-compatible', 'ollama'];
const NEEDS_MODEL: AiProviderType[] = ['anthropic', 'openai-compatible', 'ollama'];
/** Settings screen: calendar, people, rooms, AI provider and data export/import. */
export function SettingsPage() {
  const settings = useSettings();
  if (settings.isPending)
    return (
      <p role="status" className="text-muted-foreground">
        {t('app.loading')}
      </p>
    );
  if (settings.isError)
    return (
      <p role="alert" className="rounded-xl bg-destructive/10 p-4 text-destructive">
        {t('app.error')}
      </p>
    );
  return (
    <section>
      <PageHeader title={t('nav.settings')} />
      <div className="grid items-start gap-6 xl:grid-cols-2">
        <CalendarSection settings={settings.data} />
        <div className="xl:col-span-2">
          <UsersSection />
        </div>
        <RoomsSection />
        <AiProviderForm key={settings.data.updatedAt} settings={settings.data} />
        <div className="xl:col-span-2">
          <DataSection />
        </div>
      </div>
    </section>
  );
}

function AiProviderForm({ settings }: { settings: Settings }) {
  const idPrefix = useId();
  const queryClient = useQueryClient();
  const [type, setType] = useState<AiProviderType>(settings.aiProvider.type);
  const [endpoint, setEndpoint] = useState(settings.aiProvider.endpoint ?? '');
  const [model, setModel] = useState(settings.aiProvider.model ?? '');
  const [timeoutSeconds, setTimeoutSeconds] = useState(String(settings.aiProvider.timeoutSeconds ?? DEFAULT_AI_TIMEOUT_SECONDS));
  const [message, setMessage] = useState<{ kind: 'status' | 'alert'; text: string } | null>(null);

  const save = useMutation({
    mutationFn: async (aiProvider: AiProviderSettings) =>
      api.patch('/api/settings', { aiProvider }),
    onSuccess: async () => {
      setMessage({ kind: 'status', text: t('settings.saved') });
      await queryClient.invalidateQueries({ queryKey: queryKeys.settings });
    },
    onError: (error) =>
      setMessage({
        kind: 'alert',
        text: error instanceof ApiRequestError && error.code === 'validation_error' ? t('settings.ai.invalid') : t('app.error'),
      }),
  });

  const testConnection = useMutation({
    mutationFn: async (aiProvider: AiProviderSettings) => api.post<{ ok: true }>('/api/ai/test', { aiProvider }),
    onSuccess: () => setMessage({ kind: 'status', text: t('settings.ai.testSuccess') }),
    onError: (error) =>
      setMessage({
        kind: 'alert',
        text: format('settings.ai.testFailed', {
          message: error instanceof ApiRequestError ? error.message : t('app.error'),
        }),
      }),
  });

  const timeoutValue = (): number | undefined | null => {
    if (type !== 'ollama') return undefined;
    const value = Number(timeoutSeconds);
    return Number.isInteger(value) && value >= MIN_AI_TIMEOUT_SECONDS && value <= MAX_AI_TIMEOUT_SECONDS ? value : null;
  };

  const providerSettings = (ollamaTimeoutSeconds?: number): AiProviderSettings => ({
    type,
    ...(NEEDS_ENDPOINT.includes(type) && endpoint.trim() ? { endpoint: endpoint.trim() } : {}),
    ...(NEEDS_MODEL.includes(type) && model.trim() ? { model: model.trim() } : {}),
    ...(type === 'ollama' && ollamaTimeoutSeconds !== undefined ? { timeoutSeconds: ollamaTimeoutSeconds } : {}),
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    setMessage(null);
    const timeout = timeoutValue();
    if (timeout === null) {
      setMessage({ kind: 'alert', text: t('settings.ai.invalidTimeout') });
      return;
    }
    save.mutate(providerSettings(timeout));
  };

  return (
    <form className={settingsCardClass} onSubmit={submit} noValidate aria-labelledby={`${idPrefix}-title`}>
      <SettingsCardHeader
        icon={<Bot aria-hidden="true" />}
        titleId={`${idPrefix}-title`}
        title={t('settings.ai.title')}
        description={t('settings.ai.keyNote')}
      />

      <div className="grid gap-4 sm:grid-cols-2">
        <Field className="sm:col-span-2">
          <Label htmlFor={`${idPrefix}-type`}>{t('settings.ai.provider')}</Label>
          <NativeSelect id={`${idPrefix}-type`} value={type} onChange={(e) => setType(e.target.value as AiProviderType)}>
            {PROVIDERS.map((p) => (
              <option key={p} value={p}>
                {t(`settings.ai.provider.${p}` as MessageKey)}
              </option>
            ))}
          </NativeSelect>
        </Field>

        {NEEDS_ENDPOINT.includes(type) && (
          <Field>
            <Label htmlFor={`${idPrefix}-endpoint`}>{t('settings.ai.endpoint')}</Label>
            <Input
              id={`${idPrefix}-endpoint`}
              type="url"
              className="h-10 bg-card"
              value={endpoint}
              placeholder={type === 'ollama' ? 'http://localhost:11434' : 'https://api.openai.com/v1'}
              onChange={(e) => setEndpoint(e.target.value)}
            />
          </Field>
        )}

        {NEEDS_MODEL.includes(type) && (
          <Field>
            <Label htmlFor={`${idPrefix}-model`}>{t('settings.ai.model')}</Label>
            <Input
              id={`${idPrefix}-model`}
              className="h-10 bg-card"
              value={model}
              placeholder={type === 'anthropic' ? 'claude-opus-5' : ''}
              onChange={(e) => setModel(e.target.value)}
            />
          </Field>
        )}

        {type === 'ollama' && (
          <Field className="sm:col-span-2">
            <Label htmlFor={`${idPrefix}-timeout`}>{t('settings.ai.timeout')}</Label>
            <Input
              id={`${idPrefix}-timeout`}
              type="number"
              min={MIN_AI_TIMEOUT_SECONDS}
              max={MAX_AI_TIMEOUT_SECONDS}
              step="1"
              className="h-10 bg-card sm:max-w-48"
              value={timeoutSeconds}
              onChange={(e) => setTimeoutSeconds(e.target.value)}
            />
            <p className="text-sm text-muted-foreground">{t('settings.ai.timeoutHelp')}</p>
          </Field>
        )}
      </div>

      {type === 'anthropic' && (
        <div className="rounded-xl border border-primary/25 bg-primary/5 p-4 text-sm">
          <p className="font-semibold">{t('settings.ai.anthropicSetup')}</p>
          <ol className="mt-2 list-decimal space-y-1 pl-5 text-muted-foreground">
            <li>
              <a className="font-semibold text-primary underline" href="https://console.anthropic.com/settings/keys" target="_blank" rel="noreferrer">
                {t('settings.ai.anthropicStep1')}
              </a>
            </li>
            <li>
              <code>{t('settings.ai.anthropicStep2')}</code>
            </li>
            <li>{t('settings.ai.anthropicStep3')}</li>
          </ol>
        </div>
      )}

      {message && <FormMessage kind={message.kind}>{message.text}</FormMessage>}
      <FormActions>
        <Button
          type="button"
          variant="secondary"
          disabled={type === 'none' || save.isPending || testConnection.isPending}
          onClick={() => {
            setMessage(null);
            const timeout = timeoutValue();
            if (timeout === null) {
              setMessage({ kind: 'alert', text: t('settings.ai.invalidTimeout') });
              return;
            }
            testConnection.mutate(providerSettings(timeout));
          }}
        >
          <TestTube2 aria-hidden="true" />
          {testConnection.isPending ? t('settings.ai.testing') : t('settings.ai.test')}
        </Button>
        <Button type="submit" disabled={save.isPending}>
          <Save aria-hidden="true" />
          {t('common.save')}
        </Button>
      </FormActions>
    </form>
  );
}

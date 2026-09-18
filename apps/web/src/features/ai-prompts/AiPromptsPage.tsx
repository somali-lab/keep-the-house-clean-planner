import type { AiPromptTemplates } from '@huishoudplanner/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Braces, RotateCcw, Save } from 'lucide-react';
import { useId, useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/button';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { cn } from '@/lib/utils';
import { api, ApiRequestError } from '../../api/index.ts';
import { queryKeys, useSettings } from '../../api/queries.ts';
import { t, type MessageKey } from '../../i18n/nl.ts';

type PromptAction = keyof AiPromptTemplates;

interface PromptInfo {
  actions: Record<PromptAction, { system: string; user: string; dynamicData: string }>;
  defaults: AiPromptTemplates;
}

const ACTIONS: { key: PromptAction; label: MessageKey }[] = [
  { key: 'planProposal', label: 'settings.ai.prompt.proposal' },
  { key: 'planRebalance', label: 'settings.ai.prompt.rebalance' },
  { key: 'taskSuggestions', label: 'settings.ai.prompt.tasks' },
  { key: 'planExplanation', label: 'settings.ai.prompt.explanation' },
];

const ACTION_TABS: Record<PromptAction, string> = {
  planProposal: 'proposal',
  planRebalance: 'rebalance',
  taskSuggestions: 'tasks',
  planExplanation: 'explanation',
};

export function AiPromptsPage({ embedded = false }: { embedded?: boolean }) {
  const settings = useSettings();
  const promptInfo = useQuery({
    queryKey: ['ai-prompt-info'],
    queryFn: async () => (await api.get<PromptInfo>('/api/ai/prompt-info')).data,
  });

  if (settings.isPending || promptInfo.isPending) {
    return <p role="status" className="text-muted-foreground">{t('app.loading')}</p>;
  }
  if (settings.isError || promptInfo.isError) {
    return <p role="alert" className="rounded-xl bg-destructive/10 p-4 text-destructive">{t('app.error')}</p>;
  }

  const effective = Object.fromEntries(
    ACTIONS.map(({ key }) => [key, { system: promptInfo.data.actions[key].system, user: promptInfo.data.actions[key].user }]),
  ) as AiPromptTemplates;

  return (
    <PromptEditor
      embedded={embedded}
      key={settings.data.updatedAt}
      initial={settings.data.aiPromptTemplates ?? effective}
      defaults={promptInfo.data.defaults}
      info={promptInfo.data}
    />
  );
}

function PromptEditor({ initial, defaults, info, embedded }: { initial: AiPromptTemplates; defaults: AiPromptTemplates; info: PromptInfo; embedded: boolean }) {
  const id = useId();
  const queryClient = useQueryClient();
  const [searchParams, setSearchParams] = useSearchParams();
  const selected =
    ACTIONS.find(({ key }) => ACTION_TABS[key] === searchParams.get('tab'))?.key ??
    'planProposal';
  const [prompts, setPrompts] = useState<AiPromptTemplates>(initial);
  const [message, setMessage] = useState<{ kind: 'status' | 'alert'; text: string } | null>(null);

  const save = useMutation({
    mutationFn: async () => api.patch('/api/settings', { aiPromptTemplates: prompts }),
    onSuccess: async () => {
      setMessage({ kind: 'status', text: t('aiPrompts.saved') });
      await queryClient.invalidateQueries({ queryKey: queryKeys.settings });
    },
    onError: (error) =>
      setMessage({
        kind: 'alert',
        text: error instanceof ApiRequestError && error.code === 'validation_error' ? t('aiPrompts.invalid') : t('app.error'),
      }),
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    setMessage(null);
    if (Object.values(prompts).some((prompt) => !prompt.system.includes('{{schema}}') || !prompt.user.includes('{{input}}'))) {
      setMessage({ kind: 'alert', text: t('aiPrompts.invalid') });
      return;
    }
    save.mutate();
  };

  const prompt = prompts[selected];
  const update = (field: 'system' | 'user', value: string) =>
    setPrompts((current) => ({ ...current, [selected]: { ...current[selected], [field]: value } }));

  return (
    <section className="flex flex-col gap-6">
      {!embedded && <PageHeader title={t('nav.aiPrompts')} description={t('aiPrompts.intro')} />}
      <form onSubmit={submit} className="overflow-hidden rounded-2xl border bg-card shadow-sm" aria-label={t('nav.aiPrompts')}>
        <div className="flex flex-wrap items-center gap-2 border-b bg-secondary/20 p-3" role="tablist" aria-label={t('aiPrompts.chooseAction')}>
          {ACTIONS.map(({ key, label }) => (
            <Button
              key={key}
              type="button"
              role="tab"
              aria-selected={selected === key}
              variant={selected === key ? 'default' : 'ghost'}
              onClick={() => {
                const next = new URLSearchParams(searchParams);
                next.set('tab', ACTION_TABS[key]);
                setSearchParams(next);
              }}
            >
              {t(label)}
            </Button>
          ))}
        </div>

        <div className="grid gap-6 p-5 xl:grid-cols-2">
          <div className="flex min-w-0 flex-col gap-2">
            <Label htmlFor={`${id}-system`} className="text-base font-bold">{t('aiPrompts.system')}</Label>
            <p className="text-sm text-muted-foreground">{t('aiPrompts.systemHelp')}</p>
            <Textarea
              id={`${id}-system`}
              value={prompt.system}
              maxLength={20000}
              onChange={(event) => update('system', event.target.value)}
              className="min-h-[28rem] resize-y bg-background font-mono text-xs leading-relaxed"
            />
          </div>
          <div className="flex min-w-0 flex-col gap-2">
            <Label htmlFor={`${id}-user`} className="text-base font-bold">{t('aiPrompts.user')}</Label>
            <p className="text-sm text-muted-foreground">{t('aiPrompts.userHelp')}</p>
            <Textarea
              id={`${id}-user`}
              value={prompt.user}
              maxLength={20000}
              onChange={(event) => update('user', event.target.value)}
              className="min-h-[28rem] resize-y bg-background font-mono text-xs leading-relaxed"
            />
          </div>
          <div className="rounded-xl border bg-muted/30 p-4 text-sm xl:col-span-2">
            <p className="flex items-center gap-2 font-bold"><Braces className="size-4" aria-hidden="true" />{t('aiPrompts.dynamicTitle')}</p>
            <p className="mt-1 text-muted-foreground">{info.actions[selected].dynamicData}</p>
          </div>
        </div>

        <div className="flex flex-wrap items-center justify-between gap-3 border-t px-5 py-4">
          <div className="flex flex-wrap gap-2">
            <Button type="button" variant="outline" onClick={() => setPrompts((current) => ({ ...current, [selected]: { ...defaults[selected] } }))}>
              <RotateCcw aria-hidden="true" />{t('aiPrompts.resetAction')}
            </Button>
            <Button type="button" variant="ghost" onClick={() => setPrompts(structuredClone(defaults))}>
              {t('aiPrompts.resetAll')}
            </Button>
          </div>
          <Button type="submit" disabled={save.isPending}>
            <Save aria-hidden="true" />{t('common.save')}
          </Button>
        </div>
        {message && (
          <p role={message.kind} className={cn('border-t px-5 py-3 text-sm font-semibold', message.kind === 'alert' ? 'text-destructive' : 'text-success')}>
            {message.text}
          </p>
        )}
      </form>
    </section>
  );
}

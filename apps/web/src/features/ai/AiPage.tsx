import type { TaskSuggestion } from '@huishoudplanner/shared';
import { useQueryClient } from '@tanstack/react-query';
import { Bot, Check, CircleCheck, FileText, Lightbulb, Plus, Scale, Settings, Sparkles, TriangleAlert, WandSparkles } from 'lucide-react';
import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { Link } from 'react-router';
import { NativeSelect } from '@/components/NativeSelect';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/button';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { cn } from '@/lib/utils';
import { api, ApiRequestError } from '../../api/index.ts';
import { queryKeys, useRooms, useSettings } from '../../api/queries.ts';
import { format, t } from '../../i18n/nl.ts';
import { usePlans } from '../planner/api.ts';
import { useAiActions, useAiGenerationStartedAt } from './api.ts';

function errorText(error: unknown): string {
  if (error instanceof ApiRequestError) {
    if (error.code === 'ai_invalid_plan') return t('ai.error.invalidPlan');
    if (error.code === 'ai_invalid_response') return t('ai.error.invalidResponse');
    if (error.code === 'ai_disabled') return t('ai.error.disabled');
    if (error.code === 'ai_misconfigured') return t('ai.error.misconfigured');
    if (error.code === 'ai_provider_error') return t('ai.error.provider');
  }
  return t('app.error');
}

const cardClass = 'flex flex-col gap-5 rounded-2xl border bg-card p-6 text-card-foreground shadow-sm';

function CardHeading({ id, icon, children }: { id?: string; icon: ReactNode; children: ReactNode }) {
  return (
    <div className="flex items-center gap-3">
      <div className="grid size-10 shrink-0 place-items-center rounded-xl bg-accent text-accent-foreground [&_svg]:size-5">
        {icon}
      </div>
      <h2 id={id} className="text-lg font-bold">
        {children}
      </h2>
    </div>
  );
}

export function AiPage({ section = 'all', embedded = false }: { section?: 'all' | 'plan' | 'tasks'; embedded?: boolean }) {
  const idPrefix = useId();
  const settings = useSettings();
  const plans = usePlans();
  const rooms = useRooms();
  const queryClient = useQueryClient();
  const { propose, rebalance, suggestTasks, explain } = useAiActions();

  const [constraints, setConstraints] = useState('');
  const [roomId, setRoomId] = useState('');
  const [message, setMessage] = useState<{ kind: 'status' | 'alert'; text: string } | null>(null);
  const [addedSuggestions, setAddedSuggestions] = useState<string[]>([]);
  const [timerNow, setTimerNow] = useState(() => Date.now());
  const explanationRef = useRef<HTMLElement | null>(null);

  const aiStartedAt = useAiGenerationStartedAt();
  const aiWorking = aiStartedAt !== null;
  const aiElapsedSeconds = aiStartedAt === null ? 0 : Math.max(0, Math.floor((timerNow - aiStartedAt) / 1000));

  useEffect(() => {
    if (!aiWorking || aiStartedAt === null) return;
    const timer = window.setInterval(() => {
      setTimerNow(Date.now());
    }, 1000);
    return () => window.clearInterval(timer);
  }, [aiStartedAt, aiWorking]);

  if (settings.isPending || plans.isPending)
    return (
      <p role="status" className="text-muted-foreground">
        {t('app.loading')}
      </p>
    );
  if (settings.isError || plans.isError)
    return (
      <p role="alert" className="rounded-xl bg-destructive/10 p-4 text-destructive">
        {t('app.error')}
      </p>
    );

  if (settings.data.aiProvider.type === 'none') {
    return (
      <section>
        {!embedded && <PageHeader title={t('nav.ai')} />}
        <div className="flex max-w-2xl items-start gap-4 rounded-2xl border bg-muted/60 p-6">
          <div className="grid size-12 shrink-0 place-items-center rounded-2xl bg-card text-muted-foreground shadow-sm">
            <Bot className="size-6" aria-hidden="true" />
          </div>
          <div className="flex flex-col gap-4">
            <p className="text-muted-foreground">{t('ai.off')}</p>
            <p>
              <Button asChild variant="outline">
                <Link to="/settings">
                  <Settings aria-hidden="true" />
                  {t('ai.off.settingsLink')}
                </Link>
              </Button>
            </p>
          </div>
        </div>
      </section>
    );
  }

  const activePlan = plans.data.find((p) => p.active);
  const busy = aiWorking;
  const fail = (error: unknown) => setMessage({ kind: 'alert', text: errorText(error) });
  const withConstraints = constraints.trim() ? { constraints: constraints.trim() } : {};

  const addSuggestion = async (s: TaskSuggestion) => {
    try {
      await api.post('/api/tasks', { name: s.name, roomId, intervalKey: s.intervalKey, durationMinutes: s.durationMinutes, notes: s.notes });
      setAddedSuggestions((names) => [...names, s.name]);
      await queryClient.invalidateQueries({ queryKey: queryKeys.tasks });
    } catch (error) {
      fail(error);
    }
  };

  return (
    <section className="flex flex-col gap-6">
      {!embedded && <PageHeader className="mb-0" title={t('nav.ai')} description={t('ai.intro')} />}

      {section !== 'tasks' && <>
      <div className={cn(cardClass, 'bg-gradient-to-br from-card to-secondary/50')}>
        <CardHeading icon={<Bot aria-hidden="true" />}>{t('settings.ai.title')}</CardHeading>

        <div className="flex flex-col gap-2">
          <Label htmlFor={`${idPrefix}-constraints`}>{t('ai.constraints')}</Label>
          <Textarea
            id={`${idPrefix}-constraints`}
            className="min-h-24 bg-card"
            value={constraints}
            placeholder={t('ai.constraints.placeholder')}
            onChange={(e) => setConstraints(e.target.value)}
          />
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <Button
            type="button"
            size="lg"
            disabled={busy}
            onClick={() => {
              setMessage(null);
              propose.mutate(withConstraints, {
                onSuccess: () => {
                  setMessage({ kind: 'status', text: t('ai.proposed') });
                },
                onError: fail,
              });
            }}
          >
            <Sparkles aria-hidden="true" />
            {t('ai.propose')}
          </Button>
          <Button
            type="button"
            size="lg"
            variant="secondary"
            disabled={busy || !activePlan}
            onClick={() => {
              if (!activePlan) return;
              setMessage(null);
              rebalance.mutate(
                { planId: activePlan._id, ...withConstraints },
                {
                  onSuccess: () => {
                    setMessage({ kind: 'status', text: t('ai.proposed') });
                  },
                  onError: fail,
                },
              );
            }}
          >
            <Scale aria-hidden="true" />
            {t('ai.rebalance')}
          </Button>
          <Button
            type="button"
            size="lg"
            variant="outline"
            disabled={explain.isPending || !activePlan}
            onClick={() => {
              if (!activePlan) return;
              setMessage(null);
              explain.mutate(activePlan._id, {
                onSuccess: () => {
                  setMessage({ kind: 'status', text: t('ai.explain.ready') });
                  window.setTimeout(() => explanationRef.current?.scrollIntoView?.({ behavior: 'smooth', block: 'start' }), 0);
                },
                onError: fail,
              });
            }}
          >
            <Lightbulb aria-hidden="true" />
            {t('ai.explain')}
          </Button>
        </div>
        {aiWorking && (
          <p role="status" className="flex items-center gap-2 text-sm font-semibold text-primary">
            <Sparkles className="size-4 animate-pulse" aria-hidden="true" />
            {format('ai.workingSeconds', { seconds: aiElapsedSeconds })}
          </p>
        )}
        {message && (
          <p
            role={message.kind}
            className={cn(
              'flex items-center gap-2 rounded-xl px-3 py-2 text-sm font-semibold',
              message.kind === 'status' ? 'bg-success/15 text-success' : 'bg-destructive/10 text-destructive',
            )}
          >
            {message.kind === 'status' ? (
              <CircleCheck className="size-4 shrink-0" aria-hidden="true" />
            ) : (
              <TriangleAlert className="size-4 shrink-0" aria-hidden="true" />
            )}
            {message.text}
          </p>
        )}

      </div>

      {explain.data && (
        <section ref={explanationRef} aria-labelledby="explain-title" className={cardClass}>
          <CardHeading id="explain-title" icon={<Lightbulb aria-hidden="true" />}>
            {t('ai.explain.title')}
          </CardHeading>
          <ol className="grid gap-3 md:grid-cols-2">
            {explain.data.map((line, i) => (
              <li key={i} className="rounded-xl border bg-background/60 px-4 py-3 text-sm">
                {line}
              </li>
            ))}
          </ol>
        </section>
      )}
      </>}

      {section !== 'plan' && (
      <section aria-labelledby="suggest-title" className={cardClass}>
        <CardHeading id="suggest-title" icon={<WandSparkles aria-hidden="true" />}>
          {t('ai.suggest.title')}
        </CardHeading>
        <div className="flex flex-wrap items-end gap-3">
          <div className="flex w-64 flex-col gap-2">
            <Label htmlFor={`${idPrefix}-room`}>{t('tasks.field.room')}</Label>
            <NativeSelect id={`${idPrefix}-room`} value={roomId} onChange={(e) => setRoomId(e.target.value)}>
              <option value="">{t('tasks.field.choose')}</option>
              {(rooms.data ?? [])
                .filter((r) => r.active)
                .map((room) => (
                  <option key={room._id} value={room._id}>
                    {room.name}
                  </option>
                ))}
            </NativeSelect>
          </div>
          <Button
            type="button"
            variant="secondary"
            className="h-10"
            disabled={!roomId || suggestTasks.isPending}
            onClick={() => {
              suggestTasks.mutate(roomId, { onError: fail });
            }}
          >
            <WandSparkles aria-hidden="true" />
            {t('ai.suggest')}
          </Button>
        </div>
        {suggestTasks.data &&
          (suggestTasks.data.length === 0 ? (
            <p className="rounded-xl border border-dashed px-4 py-3 text-sm text-muted-foreground">{t('ai.suggest.none')}</p>
          ) : (
            <ul className="flex flex-col gap-2">
              {suggestTasks.data.map((s) => (
                <li key={s.name} className="flex flex-wrap items-center gap-3 rounded-xl border bg-background/60 px-4 py-3">
                  <FileText className="size-5 shrink-0 text-muted-foreground" aria-hidden="true" />
                  <div className="flex min-w-0 flex-1 flex-col">
                    <strong className="font-bold">{s.name}</strong>
                    <span className="text-sm text-muted-foreground">
                      {settings.data.intervals.find((i) => i.key === s.intervalKey)?.label ?? s.intervalKey} ·{' '}
                      {format('tasks.minutes', { minutes: s.durationMinutes })}
                      {s.notes ? ` · ${s.notes}` : ''}
                    </span>
                  </div>
                  {addedSuggestions.includes(s.name) ? (
                    <span className="flex items-center gap-1.5 text-sm font-semibold text-success">
                      <Check className="size-4" aria-hidden="true" />
                      {t('ai.suggest.added')}
                    </span>
                  ) : (
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={() => void addSuggestion(s)}
                      aria-label={format('ai.suggest.addNamed', { name: s.name })}
                    >
                      <Plus aria-hidden="true" />
                      {t('ai.suggest.add')}
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          ))}
      </section>
      )}
    </section>
  );
}

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
import { getLanguage, getLocale } from '../../i18n/runtime.ts';
import { useProfile } from '../../identity/index.ts';
import { usePlans } from '../planner/api.ts';
import { useAiActions, useAiGenerationStartedAt, usePlanDiff } from './api.ts';
import { ProposalReview } from './ProposalReview.tsx';

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

function proposalLabel(name: string, createdAt: string): string {
  const cleanName = /^AI-voorstel \d{4}-\d{2}-\d{2}$/.test(name)
    ? getLanguage() === 'nl'
      ? 'AI-voorstel'
      : 'AI proposal'
    : name;
  const dateTime = new Intl.DateTimeFormat(getLocale(), {
    timeZone: 'Europe/Amsterdam',
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  });
  return `${cleanName} · ${dateTime.format(new Date(createdAt)).replace(',', '')}`;
}

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

export function AiPage() {
  const idPrefix = useId();
  const settings = useSettings();
  const plans = usePlans();
  const rooms = useRooms();
  const { activeUsers } = useProfile();
  const queryClient = useQueryClient();
  const { propose, rebalance, suggestTasks, explain, apply, discard } = useAiActions();

  const [constraints, setConstraints] = useState('');
  const [selectedDraft, setSelectedDraft] = useState<string | null>(null);
  const [roomId, setRoomId] = useState('');
  const [message, setMessage] = useState<{ kind: 'status' | 'alert'; text: string } | null>(null);
  const [addedSuggestions, setAddedSuggestions] = useState<string[]>([]);
  const [timerNow, setTimerNow] = useState(() => Date.now());
  const explanationRef = useRef<HTMLElement | null>(null);

  const diff = usePlanDiff(selectedDraft);
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
        <PageHeader title={t('nav.ai')} />
        <div className="flex max-w-2xl items-start gap-4 rounded-2xl border bg-muted/60 p-6">
          <div className="grid size-12 shrink-0 place-items-center rounded-2xl bg-card text-muted-foreground shadow-sm">
            <Bot className="size-6" aria-hidden="true" />
          </div>
          <div className="flex flex-col gap-4">
            <p className="text-muted-foreground">{t('ai.off')}</p>
            <p>
              <Button asChild variant="outline">
                <Link to="/instellingen">
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
  const openDrafts = plans.data.filter((p) => p.draft && !p.discarded);
  const draft = openDrafts.find((p) => p._id === selectedDraft) ?? null;
  const busy = aiWorking || apply.isPending || discard.isPending;
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
      <PageHeader className="mb-0" title={t('nav.ai')} description={t('ai.intro')} />

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
                onSuccess: (result) => {
                  setSelectedDraft(result.planId);
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
                  onSuccess: (result) => {
                    setSelectedDraft(result.planId);
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

        {openDrafts.length > 0 && (
          <div className="flex max-w-md flex-col gap-2 border-t pt-5">
            <Label htmlFor={`${idPrefix}-draft`}>{t('ai.drafts')}</Label>
            <NativeSelect id={`${idPrefix}-draft`} value={selectedDraft ?? ''} onChange={(e) => setSelectedDraft(e.target.value || null)}>
              <option value="">{t('tasks.field.choose')}</option>
              {openDrafts.map((p) => (
                <option key={p._id} value={p._id}>
                  {proposalLabel(p.name, p.createdAt)}
                </option>
              ))}
            </NativeSelect>
          </div>
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

      {draft && diff.isPending && (
        <p role="status" className="text-muted-foreground">
          {t('app.loading')}
        </p>
      )}
      {draft && diff.data && (
        <ProposalReview
          diff={diff.data}
          users={activeUsers}
          rationale={draft.rationale}
          busy={busy}
          onApply={() =>
            apply.mutate(draft._id, {
              onSuccess: () => {
                setSelectedDraft(null);
                setMessage({ kind: 'status', text: t('ai.applied') });
              },
              onError: fail,
            })
          }
          onDiscard={() =>
            discard.mutate(draft._id, {
              onSuccess: () => {
                setSelectedDraft(null);
                setMessage({ kind: 'status', text: t('ai.discarded') });
              },
              onError: fail,
            })
          }
        />
      )}
    </section>
  );
}

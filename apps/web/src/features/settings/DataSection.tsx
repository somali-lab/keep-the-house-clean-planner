import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Database, Download, Trash2, TriangleAlert, Upload } from 'lucide-react';
import { useId, useState, type ChangeEvent } from 'react';
import { Button, buttonVariants } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { cn } from '@/lib/utils';
import { api, ApiRequestError } from '../../api/index.ts';
import { useResetStatistics } from '../stats/api.ts';
import { format, t } from '../../i18n/nl.ts';
import { Field, FormActions, FormMessage, SettingsCardHeader, settingsCardClass } from './SettingsCard.tsx';

type Message = { kind: 'status' | 'alert'; text: string } | null;

interface PendingImport {
  fileName: string;
  /** The `schemaVersion` of the file, null when it has none (the server validates fully). */
  version: number | null;
  body: Record<string, unknown>;
  counts: { users: number; tasks: number; occurrences: number };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Reads a chosen file as an export; null when it clearly is not one (the server validates fully). */
export function readExport(fileName: string, text: string): PendingImport | null {
  let body: unknown;
  try {
    body = JSON.parse(text);
  } catch {
    return null;
  }
  if (!isRecord(body) || !isRecord(body.collections)) return null;
  const collections = body.collections;
  const count = (name: string) => {
    const docs = collections[name];
    return Array.isArray(docs) ? docs.length : 0;
  };
  const version = typeof body.schemaVersion === 'number' ? body.schemaVersion : null;
  return { fileName, version, body, counts: { users: count('users'), tasks: count('tasks'), occurrences: count('occurrences') } };
}

/** JSON export download and a confirmed full import. */
export function DataSection() {
  const idPrefix = useId();
  const queryClient = useQueryClient();
  const [pending, setPending] = useState<PendingImport | null>(null);
  const [confirmReset, setConfirmReset] = useState(false);
  const [message, setMessage] = useState<Message>(null);
  const resetExecution = useResetStatistics();
  const [acknowledged, setAcknowledged] = useState(false);
  const [acknowledgedBadges, setAcknowledgedBadges] = useState(false);

  // A file older than version 5 has no redemptions, so importing it removes the ones that exist (ADR-0013).
  const olderFile = pending !== null && pending.version !== null && pending.version < 5;
  const redemptions = useQuery({
    queryKey: ['points', 'redemptions', 'count'],
    queryFn: async () => (await api.get<{ count: number }>('/api/points/redemptions/count')).data.count,
    enabled: olderFile,
    retry: false,
    gcTime: 0,
  });
  const lostRedemptions = olderFile ? (redemptions.data ?? 0) : 0;

  // A file older than version 6 has no badges, so importing it removes the ones that exist (ADR-0014).
  const olderThanBadges = pending !== null && pending.version !== null && pending.version < 6;
  const badges = useQuery({
    queryKey: ['badges', 'count'],
    queryFn: async () => (await api.get<{ badges: unknown[] }>('/api/badges')).data.badges.length,
    enabled: olderThanBadges,
    retry: false,
    gcTime: 0,
  });
  const lostBadges = olderThanBadges ? (badges.data ?? 0) : 0;
  const waitingForCount = (olderFile && redemptions.isPending) || (olderThanBadges && badges.isPending);
  const needsAcknowledgement = (lostRedemptions > 0 && !acknowledged) || (lostBadges > 0 && !acknowledgedBadges);

  const runImport = useMutation({
    mutationFn: ({ body, acknowledgeRedemptions, acknowledgeBadges }: { body: Record<string, unknown>; acknowledgeRedemptions: boolean; acknowledgeBadges: boolean }) =>
      api.post(
        `/api/import/json?mode=replace&confirm=true${acknowledgeRedemptions ? '&acknowledgeRedemptions=true' : ''}${acknowledgeBadges ? '&acknowledgeBadges=true' : ''}`,
        body,
      ),
    onSuccess: async () => {
      setPending(null);
      setMessage({ kind: 'status', text: t('settings.data.imported') });
      // Everything changed.
      await queryClient.invalidateQueries();
    },
    onError: (error) => {
      setPending(null);
      setMessage({
        kind: 'alert',
        text: error instanceof ApiRequestError && error.code === 'validation_error' ? t('settings.data.invalidFile') : t('app.error'),
      });
    },
  });

  const choose = async (event: ChangeEvent<HTMLInputElement>) => {
    const input = event.currentTarget;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    setMessage(null);
    const parsed = readExport(file.name, await file.text());
    if (!parsed) {
      setMessage({ kind: 'alert', text: t('settings.data.invalidFile') });
      return;
    }
    setAcknowledged(false);
    setAcknowledgedBadges(false);
    setPending(parsed);
  };

  return (
    <section className={settingsCardClass} aria-labelledby={`${idPrefix}-title`}>
      <SettingsCardHeader
        icon={<Database aria-hidden="true" />}
        titleId={`${idPrefix}-title`}
        title={t('settings.data.title')}
        description={t('settings.data.explainer')}
      />
      <div className="grid gap-4 md:grid-cols-2">
        <div className="flex items-center rounded-xl border bg-background/60 p-4">
          <a className={cn(buttonVariants({ variant: 'outline' }), 'h-10')} href="/api/export/json" download>
            <Download aria-hidden="true" />
            {t('settings.data.export')}
          </a>
        </div>
        <Field className="rounded-xl border bg-background/60 p-4">
          <Label htmlFor={`${idPrefix}-file`} className="font-semibold">
            <Upload className="size-4 text-primary" aria-hidden="true" />
            {t('settings.data.importLabel')}
          </Label>
          <Input
            id={`${idPrefix}-file`}
            type="file"
            accept="application/json,.json"
            className="h-10 cursor-pointer bg-card py-1.5 file:mr-3 file:rounded-md file:bg-secondary file:px-3 file:text-secondary-foreground"
            onChange={(e) => void choose(e)}
          />
        </Field>
      </div>
      <div className="flex flex-col items-start justify-between gap-4 rounded-xl border border-destructive/30 bg-destructive/5 p-4 sm:flex-row sm:items-center">
        <div>
          <p className="font-bold">{t('settings.data.resetTitle')}</p>
          <p className="text-sm text-muted-foreground">{t('settings.data.resetExplainer')}</p>
        </div>
        <Button
          type="button"
          variant="destructive"
          className="shrink-0"
          onClick={() => {
            setMessage(null);
            setConfirmReset(true);
          }}
        >
          <Trash2 aria-hidden="true" />
          {t('settings.data.resetAction')}
        </Button>
      </div>
      {message && <FormMessage kind={message.kind}>{message.text}</FormMessage>}

      <Dialog open={confirmReset} onOpenChange={setConfirmReset}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('settings.data.resetConfirmTitle')}</DialogTitle>
            <DialogDescription>{t('settings.data.resetConfirmBody')}</DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button type="button" variant="ghost" disabled={resetExecution.isPending} onClick={() => setConfirmReset(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              type="button"
              variant="destructive"
              disabled={resetExecution.isPending}
              onClick={() =>
                resetExecution.mutate(undefined, {
                  onSuccess: () => {
                    setConfirmReset(false);
                    setMessage({ kind: 'status', text: t('settings.data.resetDone') });
                  },
                })
              }
            >
              <Trash2 aria-hidden="true" />
              {t('settings.data.resetConfirm')}
            </Button>
          </DialogFooter>
          {resetExecution.isError && (
            <p role="alert" className="text-sm font-semibold text-destructive">
              {t('settings.data.resetError')}
            </p>
          )}
        </DialogContent>
      </Dialog>

      {pending && (
        <div
          role="dialog"
          aria-modal="true"
          aria-labelledby={`${idPrefix}-confirm`}
          className="flex flex-col gap-3 rounded-xl border-2 border-destructive/40 bg-destructive/5 p-5"
        >
          <div className="flex items-center gap-3">
            <div className="grid size-9 shrink-0 place-items-center rounded-full bg-destructive/15 text-destructive">
              <TriangleAlert className="size-5" aria-hidden="true" />
            </div>
            <h2 id={`${idPrefix}-confirm`} className="text-lg font-bold text-destructive">
              {t('settings.data.confirmTitle')}
            </h2>
          </div>
          <p>{format('settings.data.confirmBody', { file: pending.fileName, ...pending.counts })}</p>
          <p className="text-sm text-muted-foreground">{t('settings.data.confirmHint')}</p>
          {lostRedemptions > 0 && (
            <div className="flex flex-col gap-2 rounded-lg border border-destructive/40 bg-background/60 p-3">
              <p role="alert" className="flex items-start gap-2 font-semibold text-destructive">
                <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
                {format('settings.data.redemptionsWarning', { version: pending.version ?? '?', count: lostRedemptions })}
              </p>
              <label className="flex items-center gap-2 text-sm font-semibold">
                <input
                  type="checkbox"
                  className="size-4 shrink-0 accent-primary"
                  checked={acknowledged}
                  onChange={(event) => setAcknowledged(event.target.checked)}
                />
                {format('settings.data.redemptionsAck', { count: lostRedemptions })}
              </label>
            </div>
          )}
          {lostBadges > 0 && (
            <div className="flex flex-col gap-2 rounded-lg border border-destructive/40 bg-background/60 p-3">
              <p role="alert" className="flex items-start gap-2 font-semibold text-destructive">
                <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
                {format('settings.data.badgesWarning', { version: pending.version ?? '?', count: lostBadges })}
              </p>
              <label className="flex items-center gap-2 text-sm font-semibold">
                <input
                  type="checkbox"
                  className="size-4 shrink-0 accent-primary"
                  checked={acknowledgedBadges}
                  onChange={(event) => setAcknowledgedBadges(event.target.checked)}
                />
                {format('settings.data.badgesAck', { count: lostBadges })}
              </label>
            </div>
          )}
          {runImport.isPending && (
            <p role="status" className="text-sm font-semibold text-muted-foreground">
              {t('settings.data.importing')}
            </p>
          )}
          <FormActions>
            <Button type="button" variant="ghost" disabled={runImport.isPending} onClick={() => setPending(null)}>
              {t('common.cancel')}
            </Button>
            <Button
              type="button"
              variant="destructive"
              disabled={runImport.isPending || waitingForCount || needsAcknowledgement}
              onClick={() =>
                runImport.mutate({
                  body: pending.body,
                  acknowledgeRedemptions: lostRedemptions > 0 && acknowledged,
                  acknowledgeBadges: lostBadges > 0 && acknowledgedBadges,
                })
              }
            >
              {t('settings.data.confirm')}
            </Button>
          </FormActions>
        </div>
      )}
    </section>
  );
}

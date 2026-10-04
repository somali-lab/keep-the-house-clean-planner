import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Database, Download, Trash2, TriangleAlert, Upload } from 'lucide-react';
import { useId, useState, type ChangeEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { apiV2, ApiRequestError, unwrap } from '../../api/index.ts';
import { toInt } from '../../api/occurrence.ts';
import { format, t } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { useResetStatistics } from '../stats/api.ts';
import { useBadgeCount, useRedemptionCount } from './api.ts';
import { Field, FormActions, FormMessage, SettingsCardHeader, settingsCardClass } from './SettingsCard.tsx';

type Message = { kind: 'status' | 'alert'; text: string; details?: string[] } | null;

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

/** How many problems of an import are listed; the server reports at most 200. */
const MAX_LISTED_PROBLEMS = 5;

/** The problems of a refused file as `path: reason` lines, from the `errors` of the problem (keyed by the path in the file). */
function importProblems(error: unknown): string[] {
  if (!(error instanceof ApiRequestError) || !isRecord(error.details)) return [];
  return Object.entries(error.details)
    .flatMap(([path, reasons]) => (Array.isArray(reasons) ? reasons : [reasons]).map((reason) => `${path}: ${String(reason)}`))
    .slice(0, MAX_LISTED_PROBLEMS);
}

/** The `Content-Disposition` file name, or a plain one. */
export function fileNameOf(disposition: string | null): string {
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition ?? '');
  const raw = match?.[1];
  if (!raw) return DEFAULT_EXPORT_NAME;
  try {
    return decodeURIComponent(raw);
  } catch {
    // A name that is not valid percent-encoding is used as the server wrote it.
    return raw;
  }
}

const DEFAULT_EXPORT_NAME = 'huishoudplanner.json';

/** The body limit of the import on the server (200 MB); a larger file is refused here, before it is read. */
export const MAX_IMPORT_BYTES = 200 * 1024 * 1024;

/** How long the object URL of a download lives: the browser starts the download from it after the click. */
const REVOKE_AFTER_MS = 10_000;

/** JSON export download (administrators), a confirmed full import and the reset of the execution data. */
export function DataSection() {
  const idPrefix = useId();
  const queryClient = useQueryClient();
  const { profile } = useProfile();
  const [pending, setPending] = useState<PendingImport | null>(null);
  const [confirmReset, setConfirmReset] = useState(false);
  const [message, setMessage] = useState<Message>(null);
  const resetExecution = useResetStatistics();
  const [acknowledged, setAcknowledged] = useState(false);
  const [acknowledgedBadges, setAcknowledgedBadges] = useState(false);
  // What the server said it would remove when it refused an import that was not acknowledged (409).
  const [refused, setRefused] = useState<{ redemptions: number; badges: number }>({ redemptions: 0, badges: 0 });

  // A file older than version 5 has no redemptions, so importing it removes the ones that exist (requirements 4.12).
  const olderFile = pending !== null && pending.version !== null && pending.version < 5;
  const redemptions = useRedemptionCount(olderFile);
  const lostRedemptions = Math.max(olderFile ? (redemptions.data ?? 0) : 0, refused.redemptions);

  // A file older than version 6 has no badges, so importing it removes the ones that exist (ADR-0014).
  const olderThanBadges = pending !== null && pending.version !== null && pending.version < 6;
  const badges = useBadgeCount(olderThanBadges);
  const lostBadges = Math.max(olderThanBadges ? (badges.data ?? 0) : 0, refused.badges);
  const waitingForCount = (olderFile && redemptions.isPending) || (olderThanBadges && badges.isPending);
  const needsAcknowledgement = (lostRedemptions > 0 && !acknowledged) || (lostBadges > 0 && !acknowledgedBadges);

  const runImport = useMutation({
    mutationFn: async ({
      body,
      acknowledgeRedemptions,
      acknowledgeBadges,
    }: {
      body: Record<string, unknown>;
      acknowledgeRedemptions: boolean;
      acknowledgeBadges: boolean;
    }) =>
      unwrap(
        apiV2.POST('/api/v2/import/json', {
          params: {
            query: {
              mode: 'replace',
              confirm: 'true',
              ...(acknowledgeRedemptions ? { acknowledgeRedemptions: 'true' } : {}),
              ...(acknowledgeBadges ? { acknowledgeBadges: 'true' } : {}),
            },
          },
          body,
        }),
      ),
    onSuccess: async () => {
      setPending(null);
      setMessage({ kind: 'status', text: t('settings.data.imported') });
      // Everything changed.
      await queryClient.invalidateQueries();
    },
    onError: (error) => {
      if (error instanceof ApiRequestError && error.status === 409 && (error.code === 'redemptions_would_be_removed' || error.code === 'badges_would_be_removed')) {
        // Not written: the server wants the person to know what goes. The confirmation stays open with the count.
        const count = isRecord(error.details) ? toInt(Number(error.details.count ?? 0)) : 0;
        setRefused((current) => (error.code === 'redemptions_would_be_removed' ? { ...current, redemptions: count } : { ...current, badges: count }));
        return;
      }
      setPending(null);
      if (error instanceof ApiRequestError && error.status === 413) {
        setMessage({ kind: 'alert', text: t('settings.data.tooLarge') });
      } else if (error instanceof ApiRequestError && error.code === 'validation_error') {
        setMessage({ kind: 'alert', text: t('settings.data.invalidFile'), details: importProblems(error) });
      } else {
        setMessage({ kind: 'alert', text: t('app.error') });
      }
    },
  });

  const download = useMutation({
    mutationFn: async () => {
      const { data, response } = await (async () => {
        const answer = await apiV2.GET('/api/v2/export/json', { parseAs: 'blob' });
        if (!answer.response.ok) throw new ApiRequestError(answer.response.status, 'http_error', answer.response.statusText);
        return answer;
      })();
      // The export needs the profile header, which a plain link cannot send: the file is fetched and handed to the browser.
      const url = URL.createObjectURL(data as unknown as Blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = fileNameOf(response.headers.get('Content-Disposition'));
      anchor.style.display = 'none';
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      setTimeout(() => URL.revokeObjectURL(url), REVOKE_AFTER_MS);
    },
    onError: () => setMessage({ kind: 'alert', text: t('settings.data.exportError') }),
  });

  const choose = async (event: ChangeEvent<HTMLInputElement>) => {
    const input = event.currentTarget;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    setMessage(null);
    if (file.size > MAX_IMPORT_BYTES) {
      setMessage({ kind: 'alert', text: t('settings.data.tooLarge') });
      return;
    }
    const parsed = readExport(file.name, await file.text());
    if (!parsed) {
      setMessage({ kind: 'alert', text: t('settings.data.invalidFile') });
      return;
    }
    setAcknowledged(false);
    setAcknowledgedBadges(false);
    setRefused({ redemptions: 0, badges: 0 });
    runImport.reset();
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
        {profile?.role === 'admin' && (
          <div className="flex items-center rounded-xl border bg-background/60 p-4">
            <Button type="button" variant="outline" className="h-10" disabled={download.isPending} onClick={() => {
              setMessage(null);
              download.mutate();
            }}>
              <Download aria-hidden="true" />
              {t('settings.data.export')}
            </Button>
          </div>
        )}
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
      {message && (
        <FormMessage kind={message.kind}>
          {message.text}
          {message.details && message.details.length > 0 && (
            <ul className="mt-1 list-disc pl-5 font-normal">
              {message.details.map((line) => (
                <li key={line}>{line}</li>
              ))}
            </ul>
          )}
        </FormMessage>
      )}

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

import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Database, Download, TriangleAlert, Upload } from 'lucide-react';
import { useId, useState, type ChangeEvent } from 'react';
import { Button, buttonVariants } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { cn } from '@/lib/utils';
import { api, ApiRequestError } from '../../api/index.ts';
import { format, t } from '../../i18n/nl.ts';
import { Field, FormActions, FormMessage, SettingsCardHeader, settingsCardClass } from './SettingsCard.tsx';

type Message = { kind: 'status' | 'alert'; text: string } | null;

interface PendingImport {
  fileName: string;
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
  return { fileName, body, counts: { users: count('users'), tasks: count('tasks'), occurrences: count('occurrences') } };
}

/** JSON export download and a confirmed full import. */
export function DataSection() {
  const idPrefix = useId();
  const queryClient = useQueryClient();
  const [pending, setPending] = useState<PendingImport | null>(null);
  const [message, setMessage] = useState<Message>(null);

  const runImport = useMutation({
    mutationFn: (body: Record<string, unknown>) => api.post('/api/import/json?mode=replace&confirm=true', body),
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
      {message && <FormMessage kind={message.kind}>{message.text}</FormMessage>}

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
          {runImport.isPending && (
            <p role="status" className="text-sm font-semibold text-muted-foreground">
              {t('settings.data.importing')}
            </p>
          )}
          <FormActions>
            <Button type="button" variant="ghost" disabled={runImport.isPending} onClick={() => setPending(null)}>
              {t('common.cancel')}
            </Button>
            <Button type="button" variant="destructive" disabled={runImport.isPending} onClick={() => runImport.mutate(pending.body)}>
              {t('settings.data.confirm')}
            </Button>
          </FormActions>
        </div>
      )}
    </section>
  );
}

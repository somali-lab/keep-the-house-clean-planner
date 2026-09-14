import { useId, useMemo, useState } from 'react';
import { Download, FileDown, Info, TriangleAlert } from 'lucide-react';
import { NativeSelect } from '@/components/NativeSelect';
import { Button, buttonVariants } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { useSettings } from '../../api/queries.ts';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import { getLanguage } from '../../i18n/runtime.ts';
import { dayKeyInZone } from '../today/todayModel.ts';
import { useCycles } from './api.ts';
import {
  exportUrl,
  isGenerated,
  rangeGenerated,
  weekOptions,
  weeksOf,
  type ExportRange,
} from './exportModel.ts';

const RANGES: ExportRange[] = ['day', '1', '2', '4', 'due'];

function dayMonth(dayKey: string): string {
  const [, m, d] = dayKey.split('-');
  return `${d}-${m}`;
}

export function ExportDialog({ onClose, now }: { onClose(): void; now?: Date }) {
  const idPrefix = useId();
  const settings = useSettings();
  const cycles = useCycles();
  const todayKey = dayKeyInZone(now ?? new Date(), settings.data?.timezone ?? 'Europe/Amsterdam');
  const options = useMemo(() => weekOptions(todayKey), [todayKey]);

  const [range, setRange] = useState<ExportRange>('1');
  const [startWeek, setStartWeek] = useState('');
  const [date, setDate] = useState(todayKey);
  const [orientation, setOrientation] = useState<'portrait' | 'landscape'>('portrait');
  const [totals, setTotals] = useState(false);

  const titleId = `${idPrefix}-title`;
  const loading = settings.isPending || cycles.isPending;
  const failed = settings.isError || cycles.isError;
  const generated = cycles.data ?? [];
  const weeks = weeksOf(range);

  const optionAvailable = (monday: string) => rangeGenerated(monday, Math.max(weeks, 1), generated);
  const selected =
    options.find((o) => o.label === startWeek && optionAvailable(o.monday)) ??
    options.find((o) => optionAvailable(o.monday));

  // The due list does not depend on generated weeks.
  const available =
    range === 'due' ? true : range === 'day' ? isGenerated(date, generated) : Boolean(selected);
  const url = available
    ? exportUrl({ range, startWeek: selected?.label ?? '', date, orientation, totals, language: getLanguage() })
    : null;

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby={titleId}
      className="flex max-w-2xl flex-col gap-5 rounded-2xl border bg-card p-6 shadow-md"
    >
      <h2 id={titleId} className="flex items-center gap-2">
        <span className="grid size-8 place-items-center rounded-lg bg-primary/10 text-primary">
          <FileDown className="size-4" aria-hidden="true" />
        </span>
        {t('export.title')}
      </h2>

      {loading ? (
        <p role="status" className="text-muted-foreground">
          {t('app.loading')}
        </p>
      ) : failed ? (
        <p role="alert" className="rounded-xl bg-destructive/10 p-4 text-destructive">
          {t('app.error')}
        </p>
      ) : (
        <>
          <fieldset className="flex flex-col gap-2">
            <legend className="mb-2 text-sm font-semibold">{t('export.range')}</legend>
            <div className="flex flex-wrap gap-1 rounded-xl bg-secondary/70 p-1">
              {RANGES.map((value) => (
                <label
                  key={value}
                  className="flex cursor-pointer items-center gap-2 rounded-lg px-3 py-2 text-sm font-semibold text-secondary-foreground transition-colors hover:bg-card/70 has-[:checked]:bg-card has-[:checked]:text-primary has-[:checked]:shadow-sm has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring/50"
                >
                  <input
                    type="radio"
                    className="size-4 accent-primary"
                    name={`${idPrefix}-range`}
                    value={value}
                    checked={range === value}
                    onChange={() => setRange(value)}
                  />
                  {t(`export.range.${value}` as MessageKey)}
                </label>
              ))}
            </div>
            {range === 'due' && (
              <p className="text-sm text-muted-foreground">{t('export.dueHint')}</p>
            )}
          </fieldset>

          {(range === 'day' || weeks > 0) && (
            <div className="grid grid-cols-2 gap-4">
              {range === 'day' && (
                <div className="flex flex-col gap-2">
                  <Label htmlFor={`${idPrefix}-date`}>{t('export.date')}</Label>
                  <Input
                    id={`${idPrefix}-date`}
                    type="date"
                    className="h-10 bg-card"
                    value={date}
                    onChange={(e) => setDate(e.target.value)}
                  />
                </div>
              )}

              {weeks > 0 && (
                <div className="flex flex-col gap-2">
                  <Label htmlFor={`${idPrefix}-week`}>{t('export.startWeek')}</Label>
                  <NativeSelect
                    id={`${idPrefix}-week`}
                    value={selected?.label ?? ''}
                    onChange={(e) => setStartWeek(e.target.value)}
                  >
                    {options.map((option) => {
                      const ok = optionAvailable(option.monday);
                      return (
                        <option key={option.label} value={option.label} disabled={!ok}>
                          {format('export.weekOption', {
                            label: option.label,
                            from: dayMonth(option.monday),
                            to: dayMonth(option.sunday),
                          })}
                          {ok ? '' : t('export.notGeneratedSuffix')}
                        </option>
                      );
                    })}
                  </NativeSelect>
                </div>
              )}

              {range === '2' && (
                <div className="flex flex-col gap-2">
                  <Label htmlFor={`${idPrefix}-orientation`}>{t('export.orientation')}</Label>
                  <NativeSelect
                    id={`${idPrefix}-orientation`}
                    value={orientation}
                    onChange={(e) => setOrientation(e.target.value as 'portrait' | 'landscape')}
                  >
                    <option value="portrait">{t('export.portrait')}</option>
                    <option value="landscape">{t('export.landscape')}</option>
                  </NativeSelect>
                </div>
              )}
            </div>
          )}

          {weeks > 0 && (
            <label className="inline-flex w-fit cursor-pointer items-center gap-2 text-sm font-semibold">
              <input
                type="checkbox"
                className="size-4 accent-primary"
                checked={totals}
                onChange={(e) => setTotals(e.target.checked)}
              />
              {t('export.totals')}
            </label>
          )}

          <div className="flex flex-col gap-2 rounded-xl bg-secondary/50 px-4 py-3 text-sm text-muted-foreground">
            <p className="flex items-start gap-2" role="note">
              <Info className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
              {t('export.onlyGenerated')}
            </p>
            <p className="pl-6">{t('export.paperNote')}</p>
          </div>
          {!available && range !== 'due' && (
            <p
              role="alert"
              className="flex items-start gap-2 rounded-xl border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm font-semibold text-destructive"
            >
              <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
              {t('export.notGenerated')}
            </p>
          )}
        </>
      )}

      <div className="flex flex-wrap gap-2">
        {url ? (
          <a className={buttonVariants()} href={url} download>
            <Download aria-hidden="true" />
            {t('export.download')}
          </a>
        ) : (
          <Button type="button" disabled>
            <Download aria-hidden="true" />
            {t('export.download')}
          </Button>
        )}
        <Button type="button" variant="outline" onClick={onClose}>
          {t('common.close')}
        </Button>
      </div>
    </div>
  );
}

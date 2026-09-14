import { ChartColumnBig, Table2 } from 'lucide-react';
import { useId, useState, type ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { t } from '../../i18n/nl.ts';

export interface LegendItem {
  label: string;
  color: string;
  /** Mirror the mark: a rect for bars, a line for lines. */
  kind: 'rect' | 'line';
}

/**
 * Maps the categorical series slots used by seriesColor() (var(--series-N)) onto the
 * theme's chart tokens, so every mark, legend key and tooltip key follows the theme.
 */
const SERIES_TOKENS =
  '[--series-1:var(--chart-1)] [--series-2:var(--chart-2)] [--series-3:var(--chart-3)] [--series-4:var(--chart-4)] [--series-5:var(--chart-5)] [--series-6:oklch(0.5_0.09_195)] [--series-7:oklch(0.48_0.1_300)] [--series-8:oklch(0.52_0.07_100)]';

/** Shared SVG styling for the chart parts (was .chart-* in styles.css). */
export const chartClasses = {
  svg: 'block h-auto w-full overflow-hidden',
  grid: 'stroke-border [stroke-width:1]',
  baseline: 'stroke-muted-foreground/50 [stroke-width:1]',
  crosshair: 'stroke-muted-foreground [stroke-dasharray:3_3] [stroke-width:1]',
  axisLabel: 'fill-muted-foreground text-[11px] tabular-nums',
  categoryLabel: 'fill-foreground text-xs font-semibold',
  valueLabel: 'fill-muted-foreground text-[11px] font-semibold tabular-nums',
  mark: 'transition-[filter]',
  markHovered: 'brightness-110',
  marker: 'stroke-card [stroke-width:2]',
  hit: 'fill-transparent outline-none focus-visible:stroke-foreground focus-visible:[stroke-width:1]',
  plot: 'relative',
} as const;

/** Table styling shared by the chart table views and the stats tables. */
export const statsTableClass =
  'w-full border-collapse text-sm [&_caption]:mb-2 [&_caption]:text-left [&_caption]:text-sm [&_caption]:font-semibold [&_caption]:text-muted-foreground [&_td]:border-t [&_td]:px-3 [&_td]:py-2 [&_td]:tabular-nums [&_tbody_th]:border-t [&_tbody_th]:px-3 [&_tbody_th]:py-2 [&_tbody_th]:text-left [&_tbody_th]:font-semibold [&_thead_th]:px-3 [&_thead_th]:pb-2 [&_thead_th]:text-left [&_thead_th]:text-xs [&_thead_th]:font-bold [&_thead_th]:tracking-wide [&_thead_th]:text-muted-foreground [&_thead_th]:uppercase [&_tbody_tr]:transition-colors [&_tbody_tr:hover]:bg-secondary/40';

/**
 * Figure with title, legend and a table-view twin. The table is the
 * accessible equivalent of the chart and also carries every value.
 */
export function ChartFrame({
  title,
  caption,
  legend,
  chart,
  table,
}: {
  title: string;
  caption: string;
  legend: LegendItem[];
  chart: ReactNode;
  table: ReactNode;
}) {
  const titleId = useId();
  const [asTable, setAsTable] = useState(false);
  return (
    <figure
      className={cn(
        'm-0 flex flex-col gap-4 rounded-2xl border bg-background/50 p-5 text-foreground',
        SERIES_TOKENS,
      )}
      aria-labelledby={titleId}
    >
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="min-w-0">
          <h3 id={titleId} className="text-base font-bold">
            {title}
          </h3>
          <p className="mt-1 text-sm text-muted-foreground">{caption}</p>
        </div>
        <div className="inline-flex rounded-lg bg-secondary p-1">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            aria-pressed={asTable}
            className="bg-card font-semibold shadow-sm hover:bg-card"
            onClick={() => setAsTable((v) => !v)}
          >
            {asTable ? <ChartColumnBig aria-hidden="true" /> : <Table2 aria-hidden="true" />}
            {asTable ? t('stats.showChart') : t('stats.showTable')}
          </Button>
        </div>
      </div>
      {legend.length >= 2 && (
        <ul
          className="flex flex-wrap gap-x-4 gap-y-1 text-sm text-muted-foreground"
          aria-label={t('stats.legend')}
        >
          {legend.map((item) => (
            <li key={item.label} className="inline-flex items-center gap-1.5">
              <svg width="16" height="10" aria-hidden="true">
                {item.kind === 'rect' ? (
                  <rect x="0" y="1" width="16" height="8" rx="2" fill={item.color} />
                ) : (
                  <line
                    x1="0"
                    y1="5"
                    x2="16"
                    y2="5"
                    stroke={item.color}
                    strokeWidth="2"
                    strokeLinecap="round"
                  />
                )}
              </svg>
              <span className="font-semibold text-foreground">{item.label}</span>
            </li>
          ))}
        </ul>
      )}
      {asTable ? <div className="overflow-x-auto">{table}</div> : chart}
    </figure>
  );
}

export interface TooltipState {
  /** Anchor position as a percentage of the plot box (the SVG scales with its container). */
  x: number;
  y: number;
  rows: { label: string; value: string; color?: string }[];
}

/** Values lead, labels follow; line keys, not boxes. */
export function ChartTooltip({ tooltip }: { tooltip: TooltipState | null }) {
  if (!tooltip) return null;
  return (
    <div
      className="pointer-events-none absolute z-10 grid min-w-32 -translate-x-1/2 gap-0.5 rounded-xl border bg-popover px-3 py-2 text-xs text-popover-foreground shadow-md"
      role="status"
      style={{ left: `${tooltip.x}%`, top: `${tooltip.y}%` }}
    >
      {tooltip.rows.map((row) => (
        <div key={row.label} className="flex items-center gap-1.5">
          {row.color && (
            <svg width="12" height="8" aria-hidden="true">
              <line
                x1="0"
                y1="4"
                x2="12"
                y2="4"
                stroke={row.color}
                strokeWidth="2"
                strokeLinecap="round"
              />
            </svg>
          )}
          <strong className="font-bold tabular-nums">{row.value}</strong>
          <span className="text-muted-foreground">{row.label}</span>
        </div>
      ))}
    </div>
  );
}

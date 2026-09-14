import { useState } from 'react';
import { cn } from '@/lib/utils';
import { t } from '../../i18n/nl.ts';
import {
  ChartFrame,
  ChartTooltip,
  chartClasses,
  statsTableClass,
  type TooltipState,
} from './ChartFrame.tsx';
import { formatMinutes, niceMax, seriesColor, ticks } from './scale.ts';

export interface TrendSeries {
  id: string;
  name: string;
  /** Slot in the fixed categorical order (by entity, never by rank). */
  slot: number;
  values: number[];
  /** Extra per-point context for the table view (e.g. planned minutes). */
  secondary?: number[];
}

const WIDTH = 560;
const HEIGHT = 240;
const PAD = { left: 48, right: 110, top: 16, bottom: 40 };
const MIN_LABEL_GAP = 14;

/** Done minutes per person per cycle: one line per person, crosshair tooltip lists every series. */
export function TrendLines({
  title,
  caption,
  xLabels,
  series,
  secondaryLabel,
}: {
  title: string;
  caption: string;
  xLabels: string[];
  series: TrendSeries[];
  secondaryLabel: string;
}) {
  const [active, setActive] = useState<number | null>(null);
  const plotW = WIDTH - PAD.left - PAD.right;
  const plotH = HEIGHT - PAD.top - PAD.bottom;
  const max = niceMax(Math.max(0, ...series.flatMap((s) => s.values)));
  const step = xLabels.length > 1 ? plotW / (xLabels.length - 1) : 0;
  const x = (i: number) => PAD.left + (xLabels.length > 1 ? i * step : plotW / 2);
  const y = (v: number) => PAD.top + plotH - (v / max) * plotH;
  const last = xLabels.length - 1;

  // Direct end labels only when they don't collide; otherwise legend + tooltip + table carry identity.
  const ends = series.map((s) => ({ s, y: y(s.values[last] ?? 0) })).sort((a, b) => a.y - b.y);
  const endLabels =
    series.length <= 4 && ends.every((e, i) => i === 0 || e.y - ends[i - 1]!.y >= MIN_LABEL_GAP);

  const tooltip: TooltipState | null =
    active === null
      ? null
      : {
          x: (x(active) / WIDTH) * 100,
          y: 4,
          rows: [
            { label: xLabels[active] ?? '', value: '' },
            ...series.map((s) => ({
              label: s.name,
              value: formatMinutes(s.values[active] ?? 0),
              color: seriesColor(s.slot),
            })),
          ],
        };
  const hitWidth = Math.max(24, xLabels.length > 1 ? step : plotW);

  const chart = (
    <div
      className={cn(chartClasses.plot, 'mx-auto w-full max-w-4xl')}
      onMouseLeave={() => setActive(null)}
    >
      <svg
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        role="img"
        aria-label={title}
        className={chartClasses.svg}
      >
        {ticks(max).map((tick) => (
          <g key={tick}>
            <line
              x1={PAD.left}
              x2={PAD.left + plotW}
              y1={y(tick)}
              y2={y(tick)}
              className={chartClasses.grid}
            />
            <text
              x={PAD.left - 8}
              y={y(tick) + 4}
              textAnchor="end"
              className={chartClasses.axisLabel}
            >
              {tick}
            </text>
          </g>
        ))}
        <line
          x1={PAD.left}
          x2={PAD.left + plotW}
          y1={y(0)}
          y2={y(0)}
          className={chartClasses.baseline}
        />
        {xLabels.map((label, i) => (
          <text
            key={label}
            x={x(i)}
            y={HEIGHT - 14}
            textAnchor="middle"
            className={chartClasses.axisLabel}
          >
            {label}
          </text>
        ))}
        {active !== null && (
          <line
            x1={x(active)}
            x2={x(active)}
            y1={PAD.top}
            y2={PAD.top + plotH}
            className={chartClasses.crosshair}
          />
        )}
        {series.map((s) => (
          <g key={s.id}>
            {s.values.length > 1 && (
              <polyline
                points={s.values.map((v, i) => `${x(i)},${y(v)}`).join(' ')}
                fill="none"
                stroke={seriesColor(s.slot)}
                strokeWidth="2.5"
                strokeLinejoin="round"
                strokeLinecap="round"
              />
            )}
            {s.values.map((v, i) => (
              <circle
                key={i}
                cx={x(i)}
                cy={y(v)}
                r="4"
                fill={seriesColor(s.slot)}
                className={chartClasses.marker}
              />
            ))}
            {endLabels && (
              <text
                x={x(last) + 10}
                y={y(s.values[last] ?? 0) + 4}
                className={chartClasses.valueLabel}
              >
                {`${s.name} ${formatMinutes(s.values[last] ?? 0)}`}
              </text>
            )}
          </g>
        ))}
        {xLabels.map((label, i) => (
          <rect
            key={label}
            x={x(i) - hitWidth / 2}
            y={PAD.top}
            width={hitWidth}
            height={plotH}
            className={chartClasses.hit}
            tabIndex={0}
            aria-label={`${label}: ${series.map((s) => `${s.name} ${formatMinutes(s.values[i] ?? 0)}`).join(', ')}`}
            onMouseEnter={() => setActive(i)}
            onFocus={() => setActive(i)}
            onBlur={() => setActive(null)}
          />
        ))}
      </svg>
      <ChartTooltip tooltip={tooltip} />
    </div>
  );

  const table = (
    <table className={statsTableClass}>
      <caption className="visually-hidden">{title}</caption>
      <thead>
        <tr>
          <th scope="col">{t('stats.cycle')}</th>
          {series.map((s) => (
            <th key={s.id} scope="col">
              {s.name}
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        {xLabels.map((label, i) => (
          <tr key={label}>
            <th scope="row">{label}</th>
            {series.map((s) => (
              <td key={s.id}>
                {formatMinutes(s.values[i] ?? 0)}
                {s.secondary &&
                  ` (${secondaryLabel.toLowerCase()} ${formatMinutes(s.secondary[i] ?? 0)})`}
              </td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  );

  return (
    <ChartFrame
      title={title}
      caption={caption}
      legend={series.map((s) => ({ label: s.name, color: seriesColor(s.slot), kind: 'line' }))}
      chart={chart}
      table={table}
    />
  );
}

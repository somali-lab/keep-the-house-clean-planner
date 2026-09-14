import { useState } from 'react';
import { cn } from '@/lib/utils';
import { t } from '../../i18n/nl.ts';
import { ChartFrame, ChartTooltip, chartClasses, statsTableClass, type TooltipState } from './ChartFrame.tsx';
import { formatMinutes, niceMax, seriesColor, ticks } from './scale.ts';

export interface FairnessRow {
  userId: string;
  name: string;
  planned: number;
  done: number;
}

const WIDTH = 560;
const LABEL_W = 120;
const VALUE_W = 72;
const BAR = 16; // <= 24px thick
const GAP = 2; // surface gap between the two touching bars
const BAND_PAD = 14;
const AXIS_H = 24;

/** Rounded 4px data-end on the right, square at the baseline. */
function barPath(x0: number, y: number, width: number, height: number): string {
  const r = Math.min(4, width, height / 2);
  if (width <= 0) return '';
  const x1 = x0 + width;
  return `M${x0},${y} H${x1 - r} A${r},${r} 0 0 1 ${x1},${y + r} V${y + height - r} A${r},${r} 0 0 1 ${x1 - r},${y + height} H${x0} Z`;
}

/** Planned vs done minutes per person: horizontal grouped bars on one axis. */
export function FairnessBars({ title, caption, rows }: { title: string; caption: string; rows: FairnessRow[] }) {
  const [tooltip, setTooltip] = useState<TooltipState | null>(null);
  const [hovered, setHovered] = useState<string | null>(null);
  const measures = [
    { key: 'planned' as const, label: t('stats.planned'), color: seriesColor(0) },
    { key: 'done' as const, label: t('stats.done'), color: seriesColor(1) },
  ];
  const max = niceMax(Math.max(0, ...rows.flatMap((r) => [r.planned, r.done])));
  const plotW = WIDTH - LABEL_W - VALUE_W;
  const bandH = BAR * 2 + GAP + BAND_PAD * 2;
  const height = rows.length * bandH + AXIS_H;
  const x = (v: number) => LABEL_W + (v / max) * plotW;

  const chart = (
    <div
      className={chartClasses.plot}
      onMouseLeave={() => {
        setTooltip(null);
        setHovered(null);
      }}
    >
      <svg viewBox={`0 0 ${WIDTH} ${height}`} role="img" aria-label={title} className={chartClasses.svg}>
        {ticks(max).map((tick) => (
          <g key={tick}>
            <line x1={x(tick)} x2={x(tick)} y1={0} y2={height - AXIS_H} className={chartClasses.grid} />
            <text x={x(tick)} y={height - 6} textAnchor="middle" className={chartClasses.axisLabel}>
              {tick}
            </text>
          </g>
        ))}
        <line x1={LABEL_W} x2={LABEL_W} y1={0} y2={height - AXIS_H} className={chartClasses.baseline} />
        {rows.map((row, i) => {
          const top = i * bandH + BAND_PAD;
          return (
            <g key={row.userId}>
              <text x={LABEL_W - 8} y={top + BAR + GAP / 2 + 4} textAnchor="end" className={chartClasses.categoryLabel}>
                {row.name}
              </text>
              {measures.map((m, j) => {
                const y = top + j * (BAR + GAP);
                const value = row[m.key];
                const id = `${row.userId}-${m.key}`;
                const show = () => {
                  setHovered(id);
                  setTooltip({
                    x: (x(value) / WIDTH) * 100,
                    y: ((y - 4) / height) * 100,
                    rows: [{ label: `${row.name} · ${m.label.toLowerCase()}`, value: formatMinutes(value), color: m.color }],
                  });
                };
                return (
                  <g key={m.key}>
                    <path
                      d={barPath(LABEL_W, y, x(value) - LABEL_W, BAR)}
                      fill={m.color}
                      className={cn(chartClasses.mark, hovered === id && chartClasses.markHovered)}
                    />
                    <text x={x(value) + 6} y={y + BAR - 4} className={chartClasses.valueLabel}>
                      {formatMinutes(value)}
                    </text>
                    {/* Hit target bigger than the mark: the whole row slice, focusable. */}
                    <rect
                      x={LABEL_W}
                      y={y - GAP / 2}
                      width={plotW + VALUE_W}
                      height={BAR + GAP}
                      className={chartClasses.hit}
                      tabIndex={0}
                      aria-label={`${row.name}, ${m.label.toLowerCase()}: ${formatMinutes(value)}`}
                      onMouseEnter={show}
                      onFocus={show}
                      onBlur={() => {
                        setTooltip(null);
                        setHovered(null);
                      }}
                    />
                  </g>
                );
              })}
            </g>
          );
        })}
      </svg>
      <ChartTooltip tooltip={tooltip} />
    </div>
  );

  const table = (
    <table className={statsTableClass}>
      <caption className="visually-hidden">{title}</caption>
      <thead>
        <tr>
          <th scope="col">{t('stats.person')}</th>
          <th scope="col">{t('stats.planned')}</th>
          <th scope="col">{t('stats.done')}</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((row) => (
          <tr key={row.userId}>
            <th scope="row">{row.name}</th>
            <td>{formatMinutes(row.planned)}</td>
            <td>{formatMinutes(row.done)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );

  return (
    <ChartFrame
      title={title}
      caption={caption}
      legend={measures.map((m) => ({ label: m.label, color: m.color, kind: 'rect' }))}
      chart={chart}
      table={table}
    />
  );
}

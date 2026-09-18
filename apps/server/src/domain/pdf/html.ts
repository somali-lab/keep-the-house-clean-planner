import type { SheetLine, WeekSheet } from './sheets.ts';

export type PdfLanguage = 'nl' | 'en';

const COPY = {
  nl: {
    weekdaysLong: ['zondag', 'maandag', 'dinsdag', 'woensdag', 'donderdag', 'vrijdag', 'zaterdag'],
    weekdaysShort: ['zo', 'ma', 'di', 'wo', 'do', 'vr', 'za'],
    day: 'Dag', theme: 'Thema', through: 't/m', generated: 'Gegenereerd op',
    paperNote: 'Afvinken op papier wordt niet automatisch in de app verwerkt.',
    documentTitle: 'Keep the House Clean', cycleWeek: (week: number) => `Week ${week} van de cyclus`,
    noTasks: 'Geen taken.', room: 'Ruimte', task: 'Taak', interval: 'Interval', duration: 'Duur',
    inactive: 'inactief', allTasks: 'Alle huishoudtaken', oneTask: 'taak', manyTasks: 'taken',
    taskSummary: 'ruimte, interval en geschatte duur', taskOverview: 'Overzicht uit Keep the House Clean',
    backlog: 'Achterstand', statusOn: 'Stand van', noBacklog: 'Geen achterstand.', daysAgo: 'Dagen geleden',
    status: 'Status', planned: 'Gepland', farBehind: 'Flink achter', due: 'Aan de beurt', daySchedule: 'Dagschema',
  },
  en: {
    weekdaysLong: ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'],
    weekdaysShort: ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'],
    day: 'Day', theme: 'Theme', through: 'to', generated: 'Generated on',
    paperNote: 'Checking tasks off on paper is not automatically processed in the app.',
    documentTitle: 'Keep the House Clean', cycleWeek: (week: number) => `Week ${week} of the cycle`,
    noTasks: 'No tasks.', room: 'Room', task: 'Task', interval: 'Interval', duration: 'Duration',
    inactive: 'inactive', allTasks: 'All household tasks', oneTask: 'task', manyTasks: 'tasks',
    taskSummary: 'room, interval and estimated duration', taskOverview: 'Overview from Keep the House Clean',
    backlog: 'Overdue tasks', statusOn: 'Status on', noBacklog: 'No overdue tasks.', daysAgo: 'Days ago',
    status: 'Status', planned: 'Scheduled', farBehind: 'Significantly overdue', due: 'Due', daySchedule: 'Daily schedule',
  },
} as const;

const copy = (language: PdfLanguage = 'nl') => COPY[language];
export const PAPER_NOTE = 'Afvinken op papier wordt niet automatisch in de app verwerkt.';

export interface RenderOptions {
  orientation: 'portrait' | 'landscape';
  totals: boolean;
  language?: PdfLanguage;
  /** Already formatted in the app timezone. */
  generatedAt: string;
}

export function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);
}

function dayMonth(dayKey: string, withYear = false): string {
  const [y, m, d] = dayKey.split('-');
  return withYear ? `${d}-${m}-${y}` : `${d}-${m}`;
}

function weekdayOf(dayKey: string): number {
  const [y, m, d] = dayKey.split('-').map(Number) as [number, number, number];
  return new Date(Date.UTC(y, m - 1, d)).getUTCDay();
}

/**
 * Black-and-white safe: no information in colour. Checkbox 5mm × 5mm with a
 * 0.4mm black border. Fonts come from the system (no external requests).
 */
const STYLES = `
  * { box-sizing: border-box; }
  html, body { margin: 0; padding: 0; }
  body { font-family: "DejaVu Sans", "Liberation Sans", Arial, sans-serif; font-size: 9pt; color: #000; background: #fff; }
  .page { page-break-after: always; break-after: page; display: flex; flex-direction: column; min-height: 277mm; }
  .page:last-child { page-break-after: auto; break-after: auto; }
  .side-by-side { display: flex; gap: 6mm; }
  .side-by-side .week { flex: 1; min-width: 0; }
  header h1 { font-size: 14pt; margin: 0 0 1mm; }
  header p { margin: 0 0 1mm; }
  .theme { font-weight: bold; }
  table { width: 100%; border-collapse: collapse; table-layout: fixed; margin-top: 2mm; }
  .week table { height: 230mm; }
  .side-by-side .week table { height: 150mm; }
  .week tbody tr { height: 14.285%; }
  th, td { border: 0.3mm solid #000; padding: 1mm; vertical-align: top; text-align: left; }
  thead th { font-weight: bold; }
  tbody th { width: 22mm; font-weight: bold; }
  .line { display: flex; align-items: flex-start; gap: 1.5mm; margin-bottom: 1mm; }
  .box { display: inline-block; flex: none; width: 5mm; height: 5mm; border: 0.4mm solid #000; }
  .task { font-weight: bold; }
  .room { font-style: italic; }
  .assignee { display: block; margin-top: 0.4mm; font-size: 8pt; }
  .person-group + .person-group { margin-top: 1.5mm; padding-top: 1mm; border-top: 0.2mm solid #888; }
  .person-heading { margin-bottom: 1mm; font-size: 8pt; font-weight: bold; }
  .minutes { margin-top: 1mm; font-size: 8pt; border-top: 0.2mm dashed #000; padding-top: 0.5mm; }
  footer { margin-top: auto; padding-top: 3mm; font-size: 8pt; display: flex; justify-content: space-between; gap: 4mm; }
`;

function lineHtml(line: SheetLine, showAssignee = true): string {
  const room = line.room ? ` <span class="room">${escapeHtml(line.room)}</span>` : '';
  const assignee = showAssignee ? `<span class="assignee">${escapeHtml(line.assignee)}</span>` : '';
  return `<div class="line"><span class="box"></span><span><span class="task">${escapeHtml(line.name)}</span>${room}${assignee}</span></div>`;
}

function cellHtml(lines: SheetLine[], totals: boolean): string {
  const minutes = lines.reduce((sum, l) => sum + l.minutes, 0);
  const total = totals ? `<div class="minutes">${minutes} min</div>` : '';
  const groups = new Map<string, SheetLine[]>();
  for (const line of lines) groups.set(line.assignee, [...(groups.get(line.assignee) ?? []), line]);
  const body = [...groups.entries()].map(([assignee, personLines]) => {
    const personMinutes = personLines.reduce((sum, line) => sum + line.minutes, 0);
    return `<div class="person-group"><div class="person-heading">${escapeHtml(assignee)} · ${personMinutes} min</div>${personLines.map((line) => lineHtml(line, false)).join('')}</div>`;
  }).join('');
  return `<td>${body}${total}</td>`;
}

function tableHtml(sheet: WeekSheet, totals: boolean, language: PdfLanguage = 'nl'): string {
  const text = copy(language);
  const head = sheet.columns.map((c) => `<th>${escapeHtml(c.id === 'all' ? text.task : c.name)}</th>`).join('');
  const rows = sheet.days
    .map(
      (day) =>
        `<tr><th>${text.weekdaysLong[day.weekday]}<br>${dayMonth(day.dayKey)}</th>${day.cells.map((cell) => cellHtml(cell, totals)).join('')}</tr>`,
    )
    .join('');
  return `<table><thead><tr><th>${text.day}</th>${head}</tr></thead><tbody>${rows}</tbody></table>`;
}

function weekHeader(sheet: WeekSheet, language: PdfLanguage = 'nl'): string {
  const text = copy(language);
  const from = `${text.weekdaysShort[weekdayOf(sheet.from)]} ${dayMonth(sheet.from)}`;
  const to = `${text.weekdaysShort[weekdayOf(sheet.to)]} ${dayMonth(sheet.to, true)}`;
  const theme = sheet.theme ? `<p class="theme">${text.theme}: ${escapeHtml(sheet.theme)}</p>` : '';
  return `<header><h1>${text.cycleWeek(sheet.weekNumberInCycle)}</h1><p>${from} ${text.through} ${to} · ${sheet.isoWeek}</p>${theme}</header>`;
}

function footer(generatedAt: string, note = PAPER_NOTE, language: PdfLanguage = 'nl'): string {
  return `<footer><span>${copy(language).generated} ${escapeHtml(generatedAt)}</span><span>${escapeHtml(note)}</span></footer>`;
}

function document(body: string, extraStyles = '', language: PdfLanguage = 'nl'): string {
  return `<!doctype html><html lang="${language}"><head><meta charset="utf-8"><title>${copy(language).documentTitle}</title><style>${STYLES}${extraStyles}</style></head><body>${body}</body></html>`;
}

/** Portrait: one week per page. Landscape with two weeks: side by side on one page. */
export function scheduleDocument(sheets: WeekSheet[], options: RenderOptions): string {
  const language = options.language ?? 'nl';
  const week = (sheet: WeekSheet) => `<section class="week">${weekHeader(sheet, language)}${tableHtml(sheet, options.totals, language)}</section>`;
  if (options.orientation === 'landscape' && sheets.length === 2) {
    return document(`<div class="page"><div class="side-by-side">${sheets.map(week).join('')}</div>${footer(options.generatedAt, copy(language).paperNote, language)}</div>`, '.page { min-height: 190mm; }', language);
  }
  return document(sheets.map((sheet) => `<div class="page">${week(sheet)}${footer(options.generatedAt, copy(language).paperNote, language)}</div>`).join(''), '', language);
}

export interface DueRow {
  name: string;
  room: string | null;
  interval: string;
  daysSince: number;
  state: 'due' | 'overdue';
  /** Next planned day key, if any. */
  nextDate: string | null;
}

export interface TaskListRow {
  name: string;
  room: string;
  interval: string;
  durationMinutes: number;
  active: boolean;
}

/** Compact portrait overview. It stays on one A4 for a typical household task list and paginates if necessary. */
export function taskListDocument(rows: TaskListRow[], options: { generatedAt: string; language?: PdfLanguage }): string {
  const language = options.language ?? 'nl';
  const text = copy(language);
  const body =
    rows.length === 0
      ? `<p>${text.noTasks}</p>`
      : `<table class="task-list"><colgroup><col style="width:44mm"><col style="width:60mm"><col style="width:44mm"><col style="width:15mm"></colgroup><thead><tr><th>${text.room}</th><th>${text.task}</th><th>${text.interval}</th><th class="duration">${text.duration}</th></tr></thead><tbody>${rows
          .map(
            (row) =>
              `<tr><td>${escapeHtml(row.room)}</td><td class="task">${escapeHtml(row.name)}${row.active ? '' : ` <span class="inactive">(${text.inactive})</span>`}</td><td>${escapeHtml(row.interval)}</td><td class="duration">${row.durationMinutes} min</td></tr>`,
          )
          .join('')}</tbody></table>`;
  return document(
    `<div class="page task-list-page"><header><h1>${text.allTasks}</h1><p>${rows.length} ${rows.length === 1 ? text.oneTask : text.manyTasks} · ${text.taskSummary}</p></header>${body}${footer(options.generatedAt, text.taskOverview, language)}</div>`,
    `
      .task-list-page { font-size: 8pt; }
      .task-list-page table { width: 163mm; margin-top: 3mm; }
      .task-list-page th, .task-list-page td { padding: 0.8mm 1.2mm; vertical-align: middle; }
      .task-list-page thead { display: table-header-group; }
      .task-list-page tr { break-inside: avoid; }
      .task-list-page .inactive { font-weight: normal; font-style: italic; }
      .task-list-page .duration { text-align: right; white-space: nowrap; }
    `,
    language,
  );
}

/** Standalone "achterstand" sheet: overdue first, state spelled out (no colour). */
export function dueDocument(rows: DueRow[], options: { generatedAt: string; today: string; language?: PdfLanguage }): string {
  const language = options.language ?? 'nl';
  const text = copy(language);
  const header = `<header><h1>${text.backlog}</h1><p>${text.statusOn} ${text.weekdaysLong[weekdayOf(options.today)]} ${dayMonth(options.today, true)}</p></header>`;
  const body =
    rows.length === 0
      ? `<p>${text.noBacklog}</p>`
      : `<table><thead><tr><th style="width:8mm"></th><th>${text.task}</th><th>${text.room}</th><th>${text.interval}</th><th>${text.daysAgo}</th><th>${text.status}</th><th>${text.planned}</th></tr></thead><tbody>${rows
          .map(
            (row) =>
              `<tr><td><span class="box"></span></td><td class="task">${escapeHtml(row.name)}</td><td>${escapeHtml(row.room ?? '')}</td><td>${escapeHtml(row.interval)}</td><td>${row.daysSince}</td><td>${
                row.state === 'overdue' ? `<strong>${text.farBehind}</strong>` : text.due
              }</td><td>${row.nextDate ? `${text.weekdaysShort[weekdayOf(row.nextDate)]} ${dayMonth(row.nextDate)}` : '—'}</td></tr>`,
          )
          .join('')}</tbody></table>`;
  return document(`<div class="page">${header}${body}${footer(options.generatedAt, text.paperNote, language)}</div>`, '', language);
}

export function dayDocument(sheet: WeekSheet, options: RenderOptions): string {
  const language = options.language ?? 'nl';
  const text = copy(language);
  const day = sheet.days[0];
  const title = day ? `${text.weekdaysLong[day.weekday]} ${dayMonth(day.dayKey, true)}` : '';
  const header = `<header><h1>${text.daySchedule} ${title}</h1><p>${text.cycleWeek(sheet.weekNumberInCycle)} · ${sheet.isoWeek}</p>${
    sheet.theme ? `<p class="theme">${text.theme}: ${escapeHtml(sheet.theme)}</p>` : ''
  }</header>`;
  return document(`<div class="page">${header}${tableHtml(sheet, options.totals, language)}${footer(options.generatedAt, text.paperNote, language)}</div>`, '', language);
}

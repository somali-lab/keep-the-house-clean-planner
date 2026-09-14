import { dayKeySchema } from '@huishoudplanner/shared';
import type { FastifyPluginAsync, FastifyReply } from 'fastify';
import { DateTime } from 'luxon';
import { z } from 'zod';
import { listRooms } from '../data/rooms.ts';
import { getSettings } from '../data/settings.ts';
import { listTasks } from '../data/tasks.ts';
import { computeDueList } from '../domain/due.ts';
import { renderPdf } from '../domain/pdf/browser.ts';
import { dayDocument, dueDocument, scheduleDocument, taskListDocument } from '../domain/pdf/html.ts';
import { buildDaySheet, buildWeekSheets } from '../domain/pdf/sheets.ts';
import { parseOrThrow } from '../http/errors.ts';
import { booleanQuery } from '../http/params.ts';

const scheduleQuerySchema = z.object({
  fromWeek: z.string().regex(/^\d{4}-W\d{2}$/, 'invalid_iso_week'),
  weeks: z.enum(['1', '2', '4']).transform(Number),
  orientation: z.enum(['portrait', 'landscape']).optional(),
  totals: booleanQuery.optional(),
  language: z.enum(['nl', 'en']).optional(),
});

const dayQuerySchema = z.object({ date: dayKeySchema, language: z.enum(['nl', 'en']).optional() });
const languageQuerySchema = z.object({ language: z.enum(['nl', 'en']).optional() });

/** `huishoudschema-2026-w38.pdf`, `huishoudschema-2026-w38-w39.pdf`, `huishoudschema-2026-w52-2027-w01.pdf`. */
export function scheduleFilename(isoWeeks: string[]): string {
  const parse = (label: string) => {
    const [year, week] = label.split('-W') as [string, string];
    return { year, week: `w${week}` };
  };
  const first = parse(isoWeeks[0]!);
  const last = parse(isoWeeks.at(-1)!);
  if (isoWeeks.length === 1) return `huishoudschema-${first.year}-${first.week}.pdf`;
  if (first.year === last.year) return `huishoudschema-${first.year}-${first.week}-${last.week}.pdf`;
  return `huishoudschema-${first.year}-${first.week}-${last.year}-${last.week}.pdf`;
}

function sendPdf(reply: FastifyReply, filename: string, pdf: Buffer) {
  return reply
    .header('Content-Type', 'application/pdf')
    .header('Content-Disposition', `attachment; filename="${filename}"`)
    .send(pdf);
}

export const exportRoutes: FastifyPluginAsync = async (app) => {
  async function generatedAt(): Promise<{ label: string; timezone: string }> {
    const settings = await getSettings(app.deps.db);
    const timezone = settings?.timezone ?? app.deps.config.timezone;
    const label = DateTime.fromJSDate(app.deps.clock.now(), { zone: timezone }).toFormat('dd-LL-yyyy HH:mm');
    return { label, timezone };
  }

  app.get('/export/pdf', async (request, reply) => {
    const query = parseOrThrow(scheduleQuerySchema, request.query);
    const sheets = await buildWeekSheets(app.deps.db, query.fromWeek, query.weeks);
    const orientation = query.orientation ?? 'portrait';
    const html = scheduleDocument(sheets, {
      orientation,
      totals: query.totals ?? false,
      language: query.language,
      generatedAt: (await generatedAt()).label,
    });
    const pdf = await renderPdf(html, { landscape: orientation === 'landscape' });
    return sendPdf(reply, scheduleFilename(sheets.map((s) => s.isoWeek)), pdf);
  });

  app.get('/export/pdf/day', async (request, reply) => {
    const { date, language } = parseOrThrow(dayQuerySchema, request.query);
    const sheet = await buildDaySheet(app.deps.db, date);
    const html = dayDocument(sheet, { orientation: 'portrait', totals: true, language, generatedAt: (await generatedAt()).label });
    const pdf = await renderPdf(html, { landscape: false });
    return sendPdf(reply, `huishoudschema-${date}.pdf`, pdf);
  });

  app.get('/export/pdf/due', async (request, reply) => {
    const { language } = parseOrThrow(languageQuerySchema, request.query);
    const { today, items } = await computeDueList(app.deps.db, app.deps.clock.now());
    const rows = items
      .filter((item) => item.state !== 'ok')
      .map((item) => ({
        name: item.taskName,
        room: item.roomName,
        interval: item.intervalLabel,
        daysSince: item.daysSince,
        state: item.state as 'due' | 'overdue',
        nextDate: item.nextOccurrence?.date ?? null,
      }));
    const html = dueDocument(rows, { today, language, generatedAt: (await generatedAt()).label });
    const pdf = await renderPdf(html, { landscape: false });
    return sendPdf(reply, `achterstand-${today}.pdf`, pdf);
  });

  app.get('/export/pdf/tasks', async (request, reply) => {
    const { language } = parseOrThrow(languageQuerySchema, request.query);
    const [tasks, rooms, settings, generated] = await Promise.all([
      listTasks(app.deps.db),
      listRooms(app.deps.db),
      getSettings(app.deps.db),
      generatedAt(),
    ]);
    const roomNames = new Map(rooms.map((room) => [room._id.toHexString(), room.name]));
    const intervalLabels = new Map(settings?.intervals.map((interval) => [interval.key, interval.label]) ?? []);
    const rows = tasks
      .map((task) => ({
        name: task.name,
        room: roomNames.get(task.roomId.toHexString()) ?? (language === 'en' ? 'Unknown room' : 'Onbekende ruimte'),
        interval: intervalLabels.get(task.intervalKey) ?? task.intervalKey,
        durationMinutes: task.durationMinutes,
        active: task.active,
      }))
      .sort((a, b) => a.room.localeCompare(b.room, language === 'en' ? 'en' : 'nl') || a.name.localeCompare(b.name, language === 'en' ? 'en' : 'nl'));
    const pdf = await renderPdf(taskListDocument(rows, { generatedAt: generated.label, language }), { landscape: false });
    return sendPdf(reply, 'huishoudtaken.pdf', pdf);
  });
};

import { ObjectId } from 'mongodb';
import { PDFParse } from 'pdf-parse';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { findActivePlan } from '../src/data/cyclePlans.ts';
import { findOccurrences, updateOccurrence } from '../src/data/occurrences.ts';
import type { UserDoc } from '../src/data/users.ts';
import { escapeHtml } from '../src/domain/pdf/html.ts';
import { scheduleFilename } from '../src/routes/export.ts';
import { asProfile, seededRoom, seededUsers } from './helpers/http.ts';
import { createTestApp, type TestApp } from './helpers/testApp.ts';

// Anchor Monday 2026-09-14 (2026-W38). Nightly on that Monday generates cycles 0 and 1: W38–W45.
let t: TestApp;
let p1: UserDoc;
let p2: UserDoc;

async function parsePdf(body: Buffer): Promise<{ text: string; pages: number; pageTexts: string[] }> {
  const parser = new PDFParse({ data: body });
  try {
    const result = await parser.getText();
    return { text: result.text, pages: result.total, pageTexts: result.pages.map((p) => p.text) };
  } finally {
    await parser.destroy();
  }
}

function get(url: string) {
  return t.app.inject({ method: 'GET', url });
}

/** Text between two markers (exclusive), to check which day row a task sits in. */
function between(text: string, start: string, end: string): string {
  const from = text.indexOf(start);
  const to = text.indexOf(end, from + start.length);
  return from === -1 ? '' : text.slice(from + start.length, to === -1 ? undefined : to);
}

beforeAll(async () => {
  t = await createTestApp({ now: '2026-09-14T06:00:00.000Z' });
  [p1, p2] = await seededUsers(t);
  const headers = asProfile(p1);
  const badkamer = await seededRoom(t, 'Badkamer');
  const keuken = await seededRoom(t, 'Keuken');
  const task = async (name: string, roomId: ObjectId, intervalKey: string, durationMinutes: number) =>
    (
      await t.app.inject({
        method: 'POST',
        url: '/api/tasks',
        headers,
        payload: { name, roomId: roomId.toHexString(), intervalKey, durationMinutes },
      })
    ).json<{ _id: string }>()._id;

  const weekly = await task('Badkamer schoonmaken', badkamer._id, '1w', 30);
  const twice = await task('Wastafel', badkamer._id, '2w', 10);
  const monthly = await task('Ramen lappen', keuken._id, '4wk', 45);
  const planId = (await findActivePlan(t.db))!._id.toHexString();
  const P1 = p1._id.toHexString();
  const P2 = p2._id.toHexString();
  const put = await t.app.inject({
    method: 'PUT',
    url: `/api/cycle-plans/${planId}/slots`,
    headers,
    payload: {
      slots: [
        ...[0, 1, 2, 3].map((w) => ({ taskId: weekly, weekIndex: w, weekday: 1, assigneeId: P1 })),
        ...[0, 1, 2, 3].map((w) => ({ taskId: twice, weekIndex: w, weekday: 3, assigneeId: null })),
        { taskId: monthly, weekIndex: 1, weekday: 5, assigneeId: P2 },
      ],
    },
  });
  expect(put.statusCode, put.body).toBe(200);
  await t.app.inject({
    method: 'PATCH',
    url: `/api/cycle-plans/${planId}`,
    headers,
    payload: { weekThemes: ['', 'Keuken', '', ''] },
  });
  expect((await t.app.inject({ method: 'POST', url: '/api/jobs/nightly', headers })).statusCode).toBe(200);

  // Drag "Ramen lappen" from Friday 25 Sep to Saturday 26 Sep.
  const [ramen] = await findOccurrences(t.db, { taskId: new ObjectId(monthly) });
  await updateOccurrence(t.systemCtx(), ramen!._id, { date: new Date('2026-09-25T22:00:00Z') }, { action: 'reschedule' });

  // Check off Monday's bathroom: the sheet must still show it as a blank line.
  const [bath] = await findOccurrences(t.db, { taskId: new ObjectId(weekly) });
  const done = await t.app.inject({ method: 'PATCH', url: `/api/occurrences/${bath!._id.toHexString()}`, headers, payload: { action: 'complete' } });
  expect(done.statusCode, done.body).toBe(200);
}, 60_000);

afterAll(async () => {
  await t.close();
});

describe('GET /api/export/pdf', { timeout: 60_000 }, () => {
  it('returns a downloadable PDF with a predictable file name', async () => {
    const res = await get('/api/export/pdf?fromWeek=2026-W38&weeks=2');
    expect(res.statusCode, res.body).toBe(200);
    expect(res.headers['content-type']).toBe('application/pdf');
    expect(res.headers['content-disposition']).toBe('attachment; filename="huishoudschema-2026-w38-w39.pdf"');
    expect(res.rawPayload.subarray(0, 5).toString()).toBe('%PDF-');
  });

  it.each([
    [1, 'portrait', 1],
    [2, 'portrait', 2],
    [4, 'portrait', 4],
    [2, 'landscape', 1],
  ])('weeks=%i %s → %i page(s)', async (weeks, orientation, pages) => {
    const res = await get(`/api/export/pdf?fromWeek=2026-W38&weeks=${weeks}&orientation=${orientation}`);
    expect(res.statusCode, res.body).toBe(200);
    expect((await parsePdf(res.rawPayload)).pages).toBe(pages);
  });

  it('shows rescheduled occurrences on their new day', async () => {
    const res = await get('/api/export/pdf?fromWeek=2026-W39&weeks=1');
    const { text } = await parsePdf(res.rawPayload);
    expect(between(text, 'zaterdag', 'zondag')).toContain('Ramen lappen');
    expect(between(text, 'vrijdag', 'zaterdag')).not.toContain('Ramen lappen');
  });

  it('is a blank checklist: completed items are listed without any done marking', async () => {
    const res = await get('/api/export/pdf?fromWeek=2026-W38&weeks=1');
    const { text } = await parsePdf(res.rawPayload);
    const mondayText = between(text, 'maandag', 'dinsdag').replace(/\s+/g, ' ');
    expect(mondayText).toContain('Badkamer schoonmaken');
    for (const marker of ['done', 'gedaan', 'afgevinkt door', 'overgeslagen', '✓', '☑', '✔']) {
      expect(text.toLowerCase()).not.toContain(marker);
    }
  });

  it('prints header, theme, rooms, columns and the footer with the paper note', async () => {
    const res = await get('/api/export/pdf?fromWeek=2026-W39&weeks=1&totals=true');
    const { text } = await parsePdf(res.rawPayload);
    expect(text).toContain('Week 2 van de cyclus');
    expect(text).toContain('ma 21-09 t/m zo 27-09-2026');
    expect(text).toContain('Thema: Keuken');
    expect(text).toContain('Keuken'); // room of "Ramen lappen"
    expect(text).toContain('Persoon 1');
    expect(text.match(/Persoon 1/g)?.length).toBe(1);
    expect(text).toContain('Wie dan ook');
    expect(text).toContain('Gegenereerd op 14-09-2026 08:00');
    expect(text).toContain('Afvinken op papier wordt niet automatisch in de app verwerkt.');
    expect(text).toContain('45 min');
  });

  it('refuses weeks that have not been generated (409 with the week labels)', async () => {
    const single = await get('/api/export/pdf?fromWeek=2026-W46&weeks=1');
    expect(single.statusCode).toBe(409);
    expect(single.json()).toMatchObject({ code: 'weeks_not_generated', weeks: ['2026-W46'] });
    const partial = await get('/api/export/pdf?fromWeek=2026-W44&weeks=4');
    expect(partial.json()).toMatchObject({ code: 'weeks_not_generated', weeks: ['2026-W46', '2026-W47'] });
  });

  it('validates the query', async () => {
    for (const query of ['fromWeek=2026-W38&weeks=3', 'fromWeek=2026-38&weeks=1', 'weeks=1', 'fromWeek=2026-W38&weeks=1&orientation=diagonal', 'fromWeek=2027-W53&weeks=1']) {
      expect((await get(`/api/export/pdf?${query}`)).statusCode, query).toBe(400);
    }
  });
});

describe('GET /api/export/pdf/day and /due', { timeout: 60_000 }, () => {
  it('exports a single day on one page', async () => {
    const res = await get('/api/export/pdf/day?date=2026-09-16');
    expect(res.statusCode, res.body).toBe(200);
    expect(res.headers['content-disposition']).toBe('attachment; filename="huishoudschema-2026-09-16.pdf"');
    const { text, pages } = await parsePdf(res.rawPayload);
    expect(pages).toBe(1);
    expect(text).toContain('Dagschema woensdag 16-09-2026');
    expect(text).toContain('Wastafel');
    expect(text).not.toContain('Badkamer schoonmaken');
  });

  it('refuses a day in a week that has not been generated', async () => {
    const res = await get('/api/export/pdf/day?date=2026-11-16');
    expect(res.statusCode).toBe(409);
    expect(res.json()).toMatchObject({ code: 'weeks_not_generated', weeks: ['2026-W47'] });
  });

  it('exports the due list; nothing is due on the day the tasks were created', async () => {
    const res = await get('/api/export/pdf/due');
    expect(res.statusCode, res.body).toBe(200);
    expect(res.headers['content-disposition']).toBe('attachment; filename="achterstand-2026-09-14.pdf"');
    const { text, pages } = await parsePdf(res.rawPayload);
    expect(pages).toBe(1);
    expect(text).toContain('Achterstand');
    expect(text).toContain('Geen achterstand.');
  });
});

describe('GET /api/export/pdf/tasks', { timeout: 60_000 }, () => {
  it('exports all tasks with room, interval and duration on one A4', async () => {
    const res = await get('/api/export/pdf/tasks');
    expect(res.statusCode, res.body).toBe(200);
    expect(res.headers['content-type']).toBe('application/pdf');
    expect(res.headers['content-disposition']).toBe('attachment; filename="huishoudtaken.pdf"');
    const { text, pages } = await parsePdf(res.rawPayload);
    expect(pages).toBe(1);
    expect(text).toContain('Alle huishoudtaken');
    expect(text).toContain('Badkamer schoonmaken');
    expect(text).toContain('Badkamer');
    expect(text).toContain('1x per week');
    expect(text).toContain('30 min');
    expect(text).toContain('Ramen lappen');
    expect(text).toContain('Keuken');
    expect(text).toContain('1x per 4 weken');
    expect(text).toContain('45 min');
  });

  it('translates PDF chrome but keeps user-entered task and room names unchanged', async () => {
    const res = await get('/api/export/pdf/tasks?language=en');
    expect(res.statusCode, res.body).toBe(200);
    const { text } = await parsePdf(res.rawPayload);
    expect(text).toContain('All household tasks');
    expect(text).toContain('Room');
    expect(text).toContain('Duration');
    expect(text).toContain('Badkamer schoonmaken');
    expect(text).toContain('Badkamer');
    expect(text).not.toContain('Alle huishoudtaken');
  });
});

describe('helpers', () => {
  it('builds file names, also across a year boundary', () => {
    expect(scheduleFilename(['2026-W38'])).toBe('huishoudschema-2026-w38.pdf');
    expect(scheduleFilename(['2026-W38', '2026-W41'])).toBe('huishoudschema-2026-w38-w41.pdf');
    expect(scheduleFilename(['2026-W53', '2027-W01'])).toBe('huishoudschema-2026-w53-2027-w01.pdf');
  });

  it('escapes HTML in names', () => {
    expect(escapeHtml('<b>"Tom & Jerry\'s"</b>')).toBe('&lt;b&gt;&quot;Tom &amp; Jerry&#39;s&quot;&lt;/b&gt;');
  });
});

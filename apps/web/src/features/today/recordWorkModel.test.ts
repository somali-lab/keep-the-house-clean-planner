import { describe, expect, it } from 'vitest';
import { buildRecordWork, defaultPointsText, pointsFieldValue, type RecordWorkForm } from './recordWorkModel.ts';

const TODAY = '2026-09-16';
const FORM: RecordWorkForm = {
  kind: 'extra',
  mode: 'done',
  taskId: 't1',
  name: '',
  roomId: '',
  duration: '',
  points: null,
  doneBy: 'u1',
  date: TODAY,
  planFor: '',
};

describe('buildRecordWork', () => {
  describe('already done (today)', () => {
    it('builds an extra execution for today without touching the one-off or plan fields', () => {
      expect(buildRecordWork({ ...FORM, name: 'ignored', duration: 'x', date: '2026-01-01', planFor: 'u2' }, TODAY)).toEqual({
        ok: true,
        body: { kind: 'extra', taskId: 't1', date: TODAY, assigneeId: 'u1', done: true },
      });
    });

    it('builds a one-off task with a trimmed name, whole minutes and a null room by default', () => {
      expect(buildRecordWork({ ...FORM, kind: 'oneOff', taskId: '', name: ' Kast ophalen ', duration: ' 25 ' }, TODAY)).toEqual({
        ok: true,
        body: { kind: 'oneOff', name: 'Kast ophalen', roomId: null, durationMinutes: 25, date: TODAY, assigneeId: 'u1', done: true },
      });
    });

    it('reports every missing or invalid field', () => {
      expect(buildRecordWork({ ...FORM, taskId: '', doneBy: '' }, TODAY)).toEqual({
        ok: false,
        errors: { taskId: 'recordWork.error.task', doneBy: 'recordWork.error.doneBy' },
      });
      for (const duration of ['', '0', '-5', '1.5', 'abc']) {
        expect(buildRecordWork({ ...FORM, kind: 'oneOff', name: '  ', duration }, TODAY)).toEqual({
          ok: false,
          errors: { name: 'recordWork.error.name', duration: 'recordWork.error.duration' },
        });
      }
    });
  });

  describe('points of a one-off task', () => {
    const ONE_OFF: RecordWorkForm = { ...FORM, kind: 'oneOff', taskId: '', name: 'Kast', duration: '25' };

    it('shows the default for the duration until the field is edited by hand', () => {
      expect(defaultPointsText('25')).toBe('25');
      expect(defaultPointsText('1500')).toBe('1000');
      expect(defaultPointsText('')).toBe('');
      expect(defaultPointsText('0')).toBe('');
      expect(pointsFieldValue({ points: null, duration: '40' })).toBe('40');
      expect(pointsFieldValue({ points: '5', duration: '40' })).toBe('5');
      expect(pointsFieldValue({ points: '', duration: '40' })).toBe('');
    });

    it('omits the points while they were not edited, or cleared, so the server applies the default', () => {
      for (const points of [null, '', '  ']) {
        const result = buildRecordWork({ ...ONE_OFF, points }, TODAY);
        expect(result.ok && 'points' in result.body).toBe(false);
      }
    });

    it('sends the typed points, also 0 and the maximum, in both modes', () => {
      for (const [points, expected] of [
        ['12', 12],
        ['0', 0],
        ['1000', 1000],
      ] as const) {
        expect(buildRecordWork({ ...ONE_OFF, points }, TODAY)).toMatchObject({ ok: true, body: { points: expected } });
        expect(buildRecordWork({ ...ONE_OFF, mode: 'plan', date: '2026-09-18', points }, TODAY)).toMatchObject({
          ok: true,
          body: { points: expected, done: false },
        });
      }
    });

    it('rejects points that are not a whole number from 0 to 1000', () => {
      for (const points of ['-1', '1001', '2.5', 'abc']) {
        expect(buildRecordWork({ ...ONE_OFF, points }, TODAY)).toEqual({ ok: false, errors: { points: 'recordWork.error.points' } });
      }
    });

    it('ignores the points of an extra execution', () => {
      expect(buildRecordWork({ ...FORM, points: 'abc' }, TODAY)).toEqual({
        ok: true,
        body: { kind: 'extra', taskId: 't1', date: TODAY, assigneeId: 'u1', done: true },
      });
    });
  });

  describe('plan', () => {
    const PLAN: RecordWorkForm = { ...FORM, mode: 'plan', date: '2026-09-18', planFor: 'u2', doneBy: '' };

    it('builds an open extra execution on the chosen day for the chosen person (no done flag set)', () => {
      expect(buildRecordWork(PLAN, TODAY)).toEqual({
        ok: true,
        body: { kind: 'extra', taskId: 't1', date: '2026-09-18', assigneeId: 'u2', done: false },
      });
    });

    it('plans for anyone as assigneeId null, and accepts today', () => {
      expect(buildRecordWork({ ...PLAN, planFor: '', date: TODAY }, TODAY)).toEqual({
        ok: true,
        body: { kind: 'extra', taskId: 't1', date: TODAY, assigneeId: null, done: false },
      });
    });

    it('builds a one-off task on the chosen day, and does not need who did it', () => {
      expect(buildRecordWork({ ...PLAN, kind: 'oneOff', taskId: '', name: ' Zolder opruimen ', duration: '90', roomId: 'r1', planFor: '' }, TODAY)).toEqual({
        ok: true,
        body: { kind: 'oneOff', name: 'Zolder opruimen', roomId: 'r1', durationMinutes: 90, date: '2026-09-18', assigneeId: null, done: false },
      });
    });

    it('refuses a date before today and a missing or malformed date', () => {
      expect(buildRecordWork({ ...PLAN, date: '2026-09-15' }, TODAY)).toEqual({
        ok: false,
        errors: { date: 'recordWork.error.dateBefore' },
      });
      for (const date of ['', '16-09-2026', '2026-9-18']) {
        expect(buildRecordWork({ ...PLAN, date }, TODAY)).toEqual({ ok: false, errors: { date: 'recordWork.error.date' } });
      }
    });

    it('still validates the task and the one-off fields', () => {
      expect(buildRecordWork({ ...PLAN, taskId: '' }, TODAY)).toEqual({ ok: false, errors: { taskId: 'recordWork.error.task' } });
      expect(buildRecordWork({ ...PLAN, kind: 'oneOff', name: '', duration: '' }, TODAY)).toEqual({
        ok: false,
        errors: { name: 'recordWork.error.name', duration: 'recordWork.error.duration' },
      });
    });
  });
});

import { describe, expect, it } from 'vitest';
import { buildRecordWork, type RecordWorkForm } from './recordWorkModel.ts';

const FORM: RecordWorkForm = { kind: 'extra', taskId: 't1', name: '', roomId: '', duration: '', doneBy: 'u1' };

describe('buildRecordWork', () => {
  it('builds an extra execution for today without touching the one-off fields', () => {
    expect(buildRecordWork({ ...FORM, name: 'ignored', duration: 'x' }, '2026-09-16')).toEqual({
      ok: true,
      body: { kind: 'extra', taskId: 't1', date: '2026-09-16', assigneeId: 'u1' },
    });
  });

  it('builds a one-off task with a trimmed name, whole minutes and a null room by default', () => {
    expect(buildRecordWork({ ...FORM, kind: 'oneOff', taskId: '', name: ' Kast ophalen ', duration: ' 25 ' }, '2026-09-16')).toEqual({
      ok: true,
      body: { kind: 'oneOff', name: 'Kast ophalen', roomId: null, durationMinutes: 25, date: '2026-09-16', assigneeId: 'u1' },
    });
  });

  it('reports every missing or invalid field', () => {
    expect(buildRecordWork({ ...FORM, taskId: '', doneBy: '' }, '2026-09-16')).toEqual({
      ok: false,
      errors: { taskId: 'recordWork.error.task', doneBy: 'recordWork.error.doneBy' },
    });
    for (const duration of ['', '0', '-5', '1.5', 'abc']) {
      expect(buildRecordWork({ ...FORM, kind: 'oneOff', name: '  ', duration }, '2026-09-16')).toEqual({
        ok: false,
        errors: { name: 'recordWork.error.name', duration: 'recordWork.error.duration' },
      });
    }
  });
});

import { DEFAULT_INTERVALS } from '@huishoudplanner/shared';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ANNA, BRAM } from '../../test/fixtures.ts';
import { makeRoomV2, makeTaskV2 } from '../../test/render.tsx';
import { TaskForm } from './TaskForm.tsx';
import { emptyTaskForm, taskToForm, toTaskInput, validateTaskForm } from './taskForm.ts';

const rooms = [makeRoomV2({ id: 'r1', name: 'Keuken' })];
const limits = { minPoints: 0, maxPoints: 1000 };

function renderForm(initial = emptyTaskForm({ name: 'Ramen', roomId: 'r1', intervalKey: '4wk' }), mode: 'create' | 'edit' = 'create') {
  const onSubmit = vi.fn();
  render(
    <TaskForm
      title="Nieuwe taak"
      initial={initial}
      mode={mode}
      limits={limits}
      rooms={rooms}
      intervals={DEFAULT_INTERVALS}
      users={[ANNA, BRAM]}
      onSubmit={onSubmit}
      onCancel={() => undefined}
    />,
  );
  return {
    onSubmit,
    duration: screen.getByLabelText('Duur (minuten)'),
    points: screen.getByLabelText('Punten'),
    save: () => fireEvent.click(screen.getByRole('button', { name: 'Opslaan' })),
  };
}

describe('TaskForm points', () => {
  it('leaves the field empty for the server to default, whatever the duration is', () => {
    const { duration, points, onSubmit, save } = renderForm();
    expect(points).not.toHaveAttribute('placeholder');
    fireEvent.change(duration, { target: { value: '45' } });
    expect(points).toHaveValue(null);

    save();
    expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ durationMinutes: '45', points: '' }));
  });

  it('takes the range of the field from the limits of the server', () => {
    const { points } = renderForm();
    expect(points).toHaveAttribute('min', '0');
    expect(points).toHaveAttribute('max', '1000');
  });

  it('keeps a value typed by hand when the duration changes', () => {
    const { duration, points } = renderForm();
    fireEvent.change(duration, { target: { value: '30' } });
    fireEvent.change(points, { target: { value: '8' } });
    fireEvent.change(duration, { target: { value: '90' } });
    expect(points).toHaveValue(8);
  });

  it('shows the points of an existing task and never changes them when the duration is edited', () => {
    const task = makeTaskV2({ id: 't1', name: 'Ramen', roomId: 'r1', durationMinutes: 30, points: 8 });
    const { duration, points } = renderForm(taskToForm(task), 'edit');
    expect(points).toHaveValue(8);
    fireEvent.change(duration, { target: { value: '60' } });
    expect(points).toHaveValue(8);
  });

  it('explains the field and rejects points outside the limits without submitting', () => {
    const { duration, points, onSubmit, save } = renderForm();
    expect(points).toHaveAccessibleDescription(/Standaard één punt per minuut/);
    fireEvent.change(duration, { target: { value: '30' } });
    fireEvent.change(points, { target: { value: '1001' } });
    save();
    expect(screen.getByRole('alert')).toHaveTextContent('De punten moeten een heel getal van 0 tot 1000 zijn.');
    expect(points).toHaveAttribute('aria-invalid', 'true');
    expect(onSubmit).not.toHaveBeenCalled();

    fireEvent.change(points, { target: { value: '0' } });
    save();
    expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ points: '0' }));
  });

  it('lets an existing task hand its points back to the duration by emptying the field', () => {
    const task = makeTaskV2({ id: 't1', name: 'Ramen', roomId: 'r1', durationMinutes: 30, points: 8 });
    const { points, onSubmit, save } = renderForm(taskToForm(task), 'edit');
    expect(points).toHaveAccessibleDescription(/Laat leeg/);
    fireEvent.change(points, { target: { value: '' } });
    save();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ points: '' }));
  });
});

describe('task form model: points', () => {
  const base = emptyTaskForm({ name: 'Ramen', roomId: 'r1', intervalKey: '4wk', durationMinutes: '30' });

  it('validates the points against the limits', () => {
    expect(validateTaskForm({ ...base, points: '' }, { limits })).toEqual({});
    expect(validateTaskForm({ ...base, points: '1000' }, { limits })).toEqual({});
    expect(validateTaskForm({ ...base, points: '0' }, { limits })).toEqual({});
    expect(validateTaskForm({ ...base, points: '-1' }, { limits }).points).toBe('tasks.error.pointsInvalid');
    expect(validateTaskForm({ ...base, points: '2.5' }, { limits }).points).toBe('tasks.error.pointsInvalid');
    expect(validateTaskForm({ ...base, points: '1001' }, { limits }).points).toBe('tasks.error.pointsInvalid');
    expect(validateTaskForm({ ...base, points: '501' }, { limits: { minPoints: 0, maxPoints: 500 } }).points).toBe('tasks.error.pointsInvalid');
  });

  it('leaves the range to the server while the limits are not loaded, and still refuses a non-number', () => {
    expect(validateTaskForm({ ...base, points: '5000' }, {})).toEqual({});
    expect(validateTaskForm({ ...base, points: 'abc' }, {}).points).toBe('tasks.error.pointsInvalid');
  });

  it('accepts empty points on create and on edit (the default for the duration)', () => {
    expect(validateTaskForm({ ...base, points: '' }, { limits, mode: 'create' })).toEqual({});
    expect(validateTaskForm({ ...base, points: '' }, { limits, mode: 'edit' })).toEqual({});
  });

  it('sends an explicit null for the points only when an edit empties them', () => {
    expect(toTaskInput({ ...base, points: '' }, 'edit').points).toBeNull();
    expect('points' in toTaskInput({ ...base, points: '' }, 'create')).toBe(false);
    expect('points' in toTaskInput({ ...base, points: '' })).toBe(false);
    expect(toTaskInput({ ...base, points: '9' }, 'edit').points).toBe(9);
  });

  it('builds the exact body: points only when given, nothing sent as null where the API refuses it', () => {
    const body = toTaskInput({ ...base, durationMinutes: '45', points: '', notes: 'n', tags: ' a, b ,,' });
    expect(body).toEqual({
      name: 'Ramen',
      roomId: 'r1',
      intervalKey: '4wk',
      durationMinutes: 45,
      defaultAssigneeId: null,
      notes: 'n',
      tags: ['a', 'b'],
    });
    expect('points' in body).toBe(false);
    expect(toTaskInput({ ...base, points: '7' }).points).toBe(7);
    expect(toTaskInput({ ...base, points: '0' }).points).toBe(0);
    expect(toTaskInput({ ...base, defaultAssigneeId: 'u1' }).defaultAssigneeId).toBe('u1');
  });

  it('maps an existing task to the form, with the points the server holds', () => {
    expect(taskToForm(makeTaskV2({ id: 't1', name: 'Ramen', roomId: 'r1', durationMinutes: 30, points: 0, tags: ['a', 'b'] }))).toMatchObject({
      durationMinutes: '30',
      points: '0',
      tags: 'a, b',
      defaultAssigneeId: '',
    });
  });
});

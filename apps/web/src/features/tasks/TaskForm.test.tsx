import { DEFAULT_INTERVALS } from '@huishoudplanner/shared';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ANNA, BRAM } from '../../test/fixtures.ts';
import { makeRoom, makeTask } from '../../test/render.tsx';
import { TaskForm } from './TaskForm.tsx';
import { defaultPointsText, emptyTaskForm, taskToForm, toTaskInput, validateTaskForm } from './taskForm.ts';

const rooms = [makeRoom({ _id: 'r1', name: 'Keuken' })];

function renderForm(initial = emptyTaskForm({ name: 'Ramen', roomId: 'r1', intervalKey: '4wk' })) {
  const onSubmit = vi.fn();
  render(
    <TaskForm
      title="Nieuwe taak"
      initial={initial}
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
  it('keeps the field empty and shows the default for the duration as its placeholder', () => {
    const { duration, points, onSubmit, save } = renderForm();
    expect(points).toHaveAttribute('placeholder', '');
    fireEvent.change(duration, { target: { value: '30' } });
    expect(points).toHaveValue(null);
    expect(points).toHaveAttribute('placeholder', '3');
    fireEvent.change(duration, { target: { value: '45' } });
    expect(points).toHaveAttribute('placeholder', '5');

    save();
    expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ durationMinutes: '45', points: '' }));
  });

  it('keeps a value typed by hand when the duration changes, and gives the placeholder back when it is cleared', () => {
    const { duration, points } = renderForm();
    fireEvent.change(duration, { target: { value: '30' } });
    fireEvent.change(points, { target: { value: '8' } });
    fireEvent.change(duration, { target: { value: '90' } });
    expect(points).toHaveValue(8);
    fireEvent.change(points, { target: { value: '' } });
    expect(points).toHaveValue(null);
    expect(points).toHaveAttribute('placeholder', '9');
  });

  it('shows the points of an existing task and never changes them when the duration is edited', () => {
    const task = makeTask({ _id: 't1', name: 'Ramen', roomId: 'r1', durationMinutes: 30, points: 8 });
    const { duration, points } = renderForm(taskToForm(task));
    expect(points).toHaveValue(8);
    fireEvent.change(duration, { target: { value: '60' } });
    expect(points).toHaveValue(8);
  });

  it('does not move the points of an existing task that equal the default either', () => {
    const task = makeTask({ _id: 't1', name: 'Ramen', roomId: 'r1', durationMinutes: 30 });
    const { duration, points } = renderForm(taskToForm(task));
    expect(points).toHaveValue(3);
    fireEvent.change(duration, { target: { value: '60' } });
    expect(points).toHaveValue(3);
  });

  it('explains the field and rejects points outside 0 to 100 without submitting', () => {
    const { duration, points, onSubmit, save } = renderForm();
    expect(points).toHaveAccessibleDescription(/Standaard één punt per tien minuten/);
    fireEvent.change(duration, { target: { value: '30' } });
    fireEvent.change(points, { target: { value: '101' } });
    save();
    expect(screen.getByRole('alert')).toHaveTextContent('De punten moeten een heel getal van 0 tot 100 zijn.');
    expect(points).toHaveAttribute('aria-invalid', 'true');
    expect(onSubmit).not.toHaveBeenCalled();

    fireEvent.change(points, { target: { value: '0' } });
    save();
    expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ points: '0' }));
  });
});

describe('task form model: points', () => {
  it('derives the default text only for a valid duration', () => {
    expect(defaultPointsText('30')).toBe('3');
    expect(defaultPointsText(' 5 ')).toBe('1');
    expect(defaultPointsText('1500')).toBe('100');
    expect(defaultPointsText('')).toBe('');
    expect(defaultPointsText('0')).toBe('');
    expect(defaultPointsText('2.5')).toBe('');
  });

  it('validates the points', () => {
    const base = emptyTaskForm({ name: 'Ramen', roomId: 'r1', intervalKey: '4wk', durationMinutes: '30' });
    expect(validateTaskForm({ ...base, points: '' })).toEqual({});
    expect(validateTaskForm({ ...base, points: '100' })).toEqual({});
    expect(validateTaskForm({ ...base, points: '0' })).toEqual({});
    expect(validateTaskForm({ ...base, points: '-1' }).points).toBe('tasks.error.pointsInvalid');
    expect(validateTaskForm({ ...base, points: '2.5' }).points).toBe('tasks.error.pointsInvalid');
    expect(validateTaskForm({ ...base, points: '101' }).points).toBe('tasks.error.pointsInvalid');
  });

  it('omits empty points on create so the server defaults them, and sends the computed default on edit', () => {
    const base = emptyTaskForm({ name: 'Ramen', roomId: 'r1', intervalKey: '4wk', durationMinutes: '45' });
    expect(toTaskInput({ ...base, points: '7' }).points).toBe(7);
    expect(toTaskInput({ ...base, points: '0' }, 'edit').points).toBe(0);
    expect('points' in toTaskInput({ ...base, points: '' })).toBe(false);
    expect('points' in toTaskInput({ ...base, points: '' }, 'create')).toBe(false);
    expect(toTaskInput({ ...base, points: '' }, 'edit').points).toBe(5);
  });
});

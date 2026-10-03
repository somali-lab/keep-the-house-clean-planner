import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ANNA, BRAM } from '../../test/fixtures.ts';
import { makeRoom, makeTask } from '../../test/render.tsx';
import { DEFAULT_INTERVALS } from '@huishoudplanner/shared';
import { TaskForm } from './TaskForm.tsx';
import { defaultPointsText, emptyTaskForm, pointsEditedByHand, taskToForm, toTaskInput, validateTaskForm } from './taskForm.ts';

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
  it('prefills the points from the duration, one per ten minutes, until they are edited by hand', () => {
    const { duration, points, onSubmit, save } = renderForm();
    expect(points).toHaveValue(null);

    fireEvent.change(duration, { target: { value: '30' } });
    expect(points).toHaveValue(3);
    fireEvent.change(duration, { target: { value: '45' } });
    expect(points).toHaveValue(5);

    fireEvent.change(points, { target: { value: '8' } });
    fireEvent.change(duration, { target: { value: '90' } });
    // Edited by hand: the duration no longer moves them.
    expect(points).toHaveValue(8);

    save();
    expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ durationMinutes: '90', points: '8' }));
  });

  it('hands the points back to the duration when the field is cleared', () => {
    const { duration, points } = renderForm();
    fireEvent.change(duration, { target: { value: '30' } });
    fireEvent.change(points, { target: { value: '9' } });
    fireEvent.change(points, { target: { value: '' } });
    expect(points).toHaveValue(3);
    fireEvent.change(duration, { target: { value: '60' } });
    expect(points).toHaveValue(6);
  });

  it('shows the explicit points of an existing task and keeps them when the duration changes', () => {
    const task = makeTask({ _id: 't1', name: 'Ramen', roomId: 'r1', durationMinutes: 30, points: 8 });
    const { duration, points } = renderForm(taskToForm(task));
    expect(points).toHaveValue(8);
    fireEvent.change(duration, { target: { value: '60' } });
    expect(points).toHaveValue(8);
  });

  it('lets a task that still has the default points follow a new duration', () => {
    const task = makeTask({ _id: 't1', name: 'Ramen', roomId: 'r1', durationMinutes: 30 });
    const { duration, points } = renderForm(taskToForm(task));
    expect(points).toHaveValue(3);
    fireEvent.change(duration, { target: { value: '60' } });
    expect(points).toHaveValue(6);
  });

  it('explains the field and rejects points outside 0 to 100 without submitting', () => {
    const { duration, points, onSubmit, save } = renderForm();
    expect(screen.getByText(/Standaard één punt per tien minuten/)).toBeInTheDocument();
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

  it('knows when the points were set by hand', () => {
    expect(pointsEditedByHand(emptyTaskForm({ durationMinutes: '30', points: '3' }))).toBe(false);
    expect(pointsEditedByHand(emptyTaskForm({ durationMinutes: '30', points: '4' }))).toBe(true);
    expect(pointsEditedByHand(emptyTaskForm({ durationMinutes: '30', points: '' }))).toBe(false);
  });

  it('validates and sends the points, and omits them when blank so the server defaults them', () => {
    const base = emptyTaskForm({ name: 'Ramen', roomId: 'r1', intervalKey: '4wk', durationMinutes: '30' });
    expect(validateTaskForm({ ...base, points: '100' })).toEqual({});
    expect(validateTaskForm({ ...base, points: '0' })).toEqual({});
    expect(validateTaskForm({ ...base, points: '-1' }).points).toBe('tasks.error.pointsInvalid');
    expect(validateTaskForm({ ...base, points: '2.5' }).points).toBe('tasks.error.pointsInvalid');
    expect(validateTaskForm({ ...base, points: '101' }).points).toBe('tasks.error.pointsInvalid');
    expect(toTaskInput({ ...base, points: '7' }).points).toBe(7);
    expect(toTaskInput({ ...base, points: '0' }).points).toBe(0);
    expect('points' in toTaskInput({ ...base, points: '' })).toBe(false);
  });
});

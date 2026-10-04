import { screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ANNA } from '../../test/fixtures.ts';
import { makeOccurrenceV2, renderWithProviders } from '../../test/render.tsx';
import { OccurrenceItem } from './OccurrenceItem.tsx';

const noop = vi.fn();

function renderItem(overrides: Parameters<typeof makeOccurrenceV2>[0]) {
  renderWithProviders(
    <ul>
      <OccurrenceItem
        occurrence={makeOccurrenceV2(overrides)}
        todayKey="2026-09-16"
        roomName={undefined}
        users={[ANNA]}
        onComplete={noop}
        onUncomplete={noop}
        onRetract={noop}
        onSkip={noop}
        onClaim={noop}
        onAssign={noop}
      />
    </ul>,
  );
}

const DONE = { status: 'done', completedBy: ANNA.id, assigneeId: ANNA.id, taskNameSnapshot: 'Ramen' } as const;

describe('OccurrenceItem undo', () => {
  it('offers the retract of recorded work only on the day it was recorded', () => {
    renderItem({ id: 'o1', ...DONE, date: '2026-09-16', origin: 'adhoc', recordedDone: true });
    expect(screen.getByRole('button', { name: 'Ramen ongedaan maken' })).toBeInTheDocument();
  });

  it('offers no undo for recorded work of an earlier day', () => {
    renderItem({ id: 'o1', ...DONE, date: '2026-09-15', origin: 'adhoc', recordedDone: true });
    expect(screen.queryByRole('button', { name: 'Ramen ongedaan maken' })).not.toBeInTheDocument();
  });

  it('keeps the undo of a planned completion on earlier days', () => {
    renderItem({ id: 'o1', ...DONE, date: '2026-09-15' });
    expect(screen.getByRole('button', { name: 'Ramen ongedaan maken' })).toBeInTheDocument();
  });
});

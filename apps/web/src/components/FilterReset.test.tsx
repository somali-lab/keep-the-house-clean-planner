import { act, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FilterResetButton, FilterResetProvider, useFilterReset } from './FilterReset.tsx';

const BUTTON = 'Filters van dit scherm resetten';

function Screen({ onReset }: { onReset: () => void }) {
  const [value, setValue] = useState('changed');
  useFilterReset(() => {
    onReset();
    setValue('default');
  }, value !== 'default');
  return <p>{value}</p>;
}

function Harness({ onReset, showScreen }: { onReset: () => void; showScreen: boolean }) {
  return (
    <FilterResetProvider>
      <FilterResetButton />
      {showScreen && <Screen onReset={onReset} />}
    </FilterResetProvider>
  );
}

describe('header filter reset', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('is disabled while no screen registered filters', () => {
    render(<Harness onReset={vi.fn()} showScreen={false} />);
    const button = screen.getByRole('button', { name: BUTTON });
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute('aria-disabled', 'true');
    expect(button).toHaveAttribute('title', BUTTON);
  });

  it('resets the registered screen once, announces it and disables itself at the defaults', () => {
    const onReset = vi.fn();
    render(<Harness onReset={onReset} showScreen />);
    const button = screen.getByRole('button', { name: BUTTON });
    expect(button).toBeEnabled();
    expect(button).toHaveAttribute('aria-disabled', 'false');

    fireEvent.click(button);
    expect(onReset).toHaveBeenCalledTimes(1);
    expect(screen.getByText('default')).toBeInTheDocument();
    expect(screen.getByText('Filters gereset')).toBeInTheDocument();
    expect(button).toBeDisabled();

    act(() => {
      vi.advanceTimersByTime(5000);
    });
    expect(screen.queryByText('Filters gereset')).not.toBeInTheDocument();
  });

  it('drops the registration when the screen goes away', () => {
    const { rerender } = render(<Harness onReset={vi.fn()} showScreen />);
    expect(screen.getByRole('button', { name: BUTTON })).toBeEnabled();
    rerender(<Harness onReset={vi.fn()} showScreen={false} />);
    expect(screen.getByRole('button', { name: BUTTON })).toBeDisabled();
  });

  it('is a no-op for a screen rendered without the provider', () => {
    render(<Screen onReset={vi.fn()} />);
    expect(screen.getByText('changed')).toBeInTheDocument();
  });
});

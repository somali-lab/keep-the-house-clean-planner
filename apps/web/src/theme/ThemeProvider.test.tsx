import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ThemeSwitcher } from '../components/ThemeSwitcher.tsx';
import { ThemeProvider } from './ThemeProvider.tsx';

afterEach(() => {
  document.documentElement.classList.remove('dark');
  delete document.documentElement.dataset.theme;
  document.documentElement.style.removeProperty('color-scheme');
});

describe('ThemeProvider', () => {
  it('switches between light and dark and remembers the choice', () => {
    render(
      <ThemeProvider>
        <ThemeSwitcher />
      </ThemeProvider>,
    );

    expect(screen.getByRole('button', { name: 'Systeemthema' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    fireEvent.click(screen.getByRole('button', { name: 'Donker thema' }));
    expect(document.documentElement).toHaveClass('dark');
    expect(document.documentElement).toHaveAttribute('data-theme', 'dark');
    expect(window.localStorage.getItem('huishoudplanner.theme')).toBe('dark');

    fireEvent.click(screen.getByRole('button', { name: 'Licht thema' }));
    expect(document.documentElement).not.toHaveClass('dark');
    expect(document.documentElement).toHaveAttribute('data-theme', 'light');
  });

  it('follows operating-system changes in system mode', () => {
    let listener: ((event: MediaQueryListEvent) => void) | undefined;
    vi.spyOn(window, 'matchMedia').mockImplementation(
      () =>
        ({
          matches: false,
          media: '(prefers-color-scheme: dark)',
          onchange: null,
          addEventListener: (_type: string, next: (event: MediaQueryListEvent) => void) => {
            listener = next;
          },
          removeEventListener: vi.fn(),
          addListener: vi.fn(),
          removeListener: vi.fn(),
          dispatchEvent: vi.fn(),
        }) as unknown as MediaQueryList,
    );

    render(
      <ThemeProvider>
        <ThemeSwitcher />
      </ThemeProvider>,
    );
    expect(document.documentElement).not.toHaveClass('dark');

    act(() => listener?.({ matches: true } as MediaQueryListEvent));
    expect(document.documentElement).toHaveClass('dark');
    expect(document.documentElement).toHaveAttribute('data-theme', 'system');
  });
});

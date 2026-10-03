import { FilterX } from 'lucide-react';
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { t } from '../i18n/nl.ts';

interface Registration {
  /** Resets every filter of the screen that registered it. */
  run: () => void;
  /** True while at least one filter differs from its default. */
  isActive: boolean;
}

type Register = (registration: Registration) => () => void;

interface ButtonState {
  registration: Registration | null;
  announcement: string;
  reset: () => void;
}

const ANNOUNCEMENT_MS = 4000;

// Two contexts so a page that registers (stable function) never re-renders because
// the header button's state changed.
const RegisterContext = createContext<Register | null>(null);
const ButtonContext = createContext<ButtonState | null>(null);

/** Connects the screen on display to the single reset button in the header. */
export function FilterResetProvider({ children }: { children: ReactNode }) {
  const [registration, setRegistration] = useState<Registration | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => () => {
    if (timer.current) clearTimeout(timer.current);
  }, []);

  const register = useCallback<Register>((next) => {
    setRegistration(next);
    return () => setRegistration((current) => (current === next ? null : current));
  }, []);

  const reset = useCallback(() => {
    if (!registration?.isActive) return;
    registration.run();
    setAnnouncement(t('layout.filtersReset'));
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => setAnnouncement(''), ANNOUNCEMENT_MS);
  }, [registration]);

  const buttonState = useMemo(
    () => ({ registration, announcement, reset }),
    [registration, announcement, reset],
  );

  return (
    <RegisterContext.Provider value={register}>
      <ButtonContext.Provider value={buttonState}>{children}</ButtonContext.Provider>
    </RegisterContext.Provider>
  );
}

/**
 * Registers the reset handler of the current screen. `isActive` says whether any filter differs
 * from its default; the header button is only enabled then. Registration ends when the screen
 * unmounts (route change). Without a provider (isolated tests) this does nothing.
 */
export function useFilterReset(reset: () => void, isActive: boolean) {
  const register = useContext(RegisterContext);
  const latest = useRef(reset);
  useEffect(() => {
    latest.current = reset;
  });
  const run = useCallback(() => latest.current(), []);

  useEffect(() => {
    if (!register) return;
    return register({ run, isActive });
  }, [register, run, isActive]);
}

/** The one icon button in the header that resets the filters of the screen you are on. */
export function FilterResetButton({ className }: { className?: string }) {
  const state = useContext(ButtonContext);
  const enabled = Boolean(state?.registration?.isActive);

  return (
    <>
      <Button
        type="button"
        variant="ghost"
        size="icon-lg"
        className={cn('rounded-full text-muted-foreground', className)}
        aria-label={t('layout.resetFilters')}
        title={t('layout.resetFilters')}
        disabled={!enabled}
        aria-disabled={!enabled}
        onClick={state?.reset}
      >
        <FilterX aria-hidden="true" />
      </Button>
      <span className="visually-hidden" aria-live="polite">
        {state?.announcement}
      </span>
    </>
  );
}

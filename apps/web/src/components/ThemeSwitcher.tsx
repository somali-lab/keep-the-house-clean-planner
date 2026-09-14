import { Monitor, Moon, Sun, type LucideIcon } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { t, type MessageKey } from '../i18n/nl.ts';
import { useTheme, type ThemeMode } from '../theme/ThemeProvider.tsx';

const options: { mode: ThemeMode; label: MessageKey; icon: LucideIcon }[] = [
  { mode: 'light', label: 'theme.light', icon: Sun },
  { mode: 'dark', label: 'theme.dark', icon: Moon },
  { mode: 'system', label: 'theme.system', icon: Monitor },
];

export function ThemeSwitcher() {
  const { mode, setMode } = useTheme();

  return (
    <div
      className="inline-flex shrink-0 rounded-xl border bg-card p-1 shadow-xs"
      role="group"
      aria-label={t('theme.label')}
    >
      {options.map(({ mode: option, label, icon: Icon }) => (
        <Button
          key={option}
          type="button"
          size="icon-sm"
          variant="ghost"
          className={cn(
            'rounded-lg text-muted-foreground',
            mode === option && 'bg-accent text-foreground',
          )}
          aria-label={t(label)}
          title={t(label)}
          aria-pressed={mode === option}
          onClick={() => setMode(option)}
        >
          <Icon aria-hidden="true" />
        </Button>
      ))}
    </div>
  );
}

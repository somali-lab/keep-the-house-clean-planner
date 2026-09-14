import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { t } from '../i18n/nl.ts';
import { useLanguage } from '../i18n/LanguageProvider.tsx';
import type { Language } from '../i18n/runtime.ts';

const options: { language: Language; shortLabel: string }[] = [
  { language: 'nl', shortLabel: 'NL' },
  { language: 'en', shortLabel: 'EN' },
];

export function LanguageSwitcher() {
  const { language, setLanguage } = useLanguage();

  return (
    <div
      className="inline-flex shrink-0 rounded-xl border bg-card p-1 shadow-xs"
      role="group"
      aria-label={t('language.label')}
    >
      {options.map(({ language: option, shortLabel }) => (
        <Button
          key={option}
          type="button"
          size="sm"
          variant="ghost"
          className={cn(
            'h-8 rounded-lg px-2.5 text-xs text-muted-foreground',
            language === option && 'bg-accent text-foreground',
          )}
          aria-label={t(`language.${option}`)}
          title={t(`language.${option}`)}
          aria-pressed={language === option}
          onClick={() => setLanguage(option)}
        >
          {shortLabel}
        </Button>
      ))}
    </div>
  );
}

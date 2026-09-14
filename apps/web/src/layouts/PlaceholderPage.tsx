import { PageHeader } from '@/components/PageHeader';
import { EmptyState } from '@/components/EmptyState';
import { t, type MessageKey } from '../i18n/nl.ts';

export function PlaceholderPage({ titleKey }: { titleKey: MessageKey }) {
  return (
    <section>
      <PageHeader title={t(titleKey)} />
      <EmptyState>{t('page.comingSoon')}</EmptyState>
    </section>
  );
}

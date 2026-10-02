import type { ReactNode } from 'react';
import { ExternalLink } from 'lucide-react';
import { PageHeader } from '@/components/PageHeader';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import { getLocale } from '../../i18n/runtime.ts';
import { APP_OFFICIAL_BUILD, APP_RELEASE_DATE, APP_SOURCE_REF, APP_VERSION } from '../../version.ts';

// Keep in step with the GitHub repository and the LICENSE file.
const REPOSITORY_URL = 'https://github.com/somali-lab/keep-the-house-clean-planner';
const LICENSE_NAME = 'MIT';

export interface BuildInfo {
  version: string;
  official: boolean;
  releaseDate: string | null;
  sourceRef: string;
}

const RUNNING_BUILD: BuildInfo = {
  version: APP_VERSION,
  official: APP_OFFICIAL_BUILD,
  releaseDate: APP_RELEASE_DATE,
  sourceRef: APP_SOURCE_REF,
};

export function formatReleaseMoment(iso: string, locale: string = getLocale(), timeZone?: string): string {
  return new Intl.DateTimeFormat(locale, { dateStyle: 'long', timeStyle: 'short', timeZone }).format(new Date(iso))
    + ` (${new Intl.DateTimeFormat(locale, { timeZoneName: 'short', timeZone }).formatToParts(new Date(iso))
      .find((part) => part.type === 'timeZoneName')?.value ?? ''})`;
}

function Row({ label, children }: { label: MessageKey; children: ReactNode }) {
  return (
    <div className="grid gap-1 sm:grid-cols-[12rem_minmax(0,1fr)] sm:gap-4">
      <dt className="font-semibold text-muted-foreground">{t(label)}</dt>
      <dd className="min-w-0">{children}</dd>
    </div>
  );
}

function ExternalFileLink({ href, children }: { href: string; children: ReactNode }) {
  return (
    <a
      className="inline-flex items-center gap-1 font-semibold text-primary underline-offset-4 hover:underline"
      href={href}
      target="_blank"
      rel="noopener noreferrer"
    >
      {children}{' '}
      <span className="visually-hidden">({t('about.opensInNewTab')})</span>
      <ExternalLink className="size-4" aria-hidden="true" />
    </a>
  );
}

export function AboutPage({ build = RUNNING_BUILD, timeZone }: { build?: BuildInfo; timeZone?: string }) {
  const fileUrl = (file: string) => `${REPOSITORY_URL}/blob/${build.sourceRef}/${file}`;
  const releaseText = build.releaseDate
    ? formatReleaseMoment(build.releaseDate, getLocale(), timeZone)
    : t(build.official ? 'about.releaseUnknown' : 'about.releaseLocal');

  return (
    <section className="flex max-w-2xl flex-col gap-6">
      <PageHeader title={t('about.title')} description={t('about.description')} className="mb-0" />
      <dl className="grid gap-4 rounded-2xl border bg-card p-5 shadow-sm">
        <Row label="about.version">
          <span className="font-mono break-all">{build.version}</span>
        </Row>
        <Row label="about.lastRelease">{releaseText}</Row>
        <Row label="about.license">
          <ExternalFileLink href={fileUrl('LICENSE')}>
            {format('about.licenseLink', { license: LICENSE_NAME })}
          </ExternalFileLink>
        </Row>
        <Row label="about.changelog">
          <ExternalFileLink href={fileUrl('CHANGELOG.md')}>{t('about.changelogLink')}</ExternalFileLink>
        </Row>
      </dl>
    </section>
  );
}

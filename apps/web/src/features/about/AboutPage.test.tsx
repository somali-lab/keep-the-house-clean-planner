import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AboutPage } from './AboutPage.tsx';

const REPO = 'https://github.com/somali-lab/keep-the-house-clean-planner/blob';

describe('AboutPage', () => {
  it('shows the release moment of an official image and links to its tagged files', () => {
    render(
      <AboutPage
        build={{ version: '1.6.2', official: true, releaseDate: '2026-09-28T12:00:00.000Z', sourceRef: 'v1.6.2' }}
        timeZone="Europe/Amsterdam"
      />,
    );

    expect(screen.getByRole('heading', { level: 1, name: 'Over Keep the House Clean' })).toBeInTheDocument();
    expect(screen.getByText('1.6.2')).toBeInTheDocument();
    expect(screen.getByText('Laatste release').nextElementSibling).toHaveTextContent('28 september 2026 om 14:00 (CEST)');
    expect(screen.getByRole('link', { name: /^MIT-licentie bekijken\s+\(opent in een nieuw tabblad\)$/ })).toHaveAttribute('href', `${REPO}/v1.6.2/LICENSE`);
    expect(screen.getByRole('link', { name: /^Changelog bekijken\s+\(opent in een nieuw tabblad\)$/ })).toHaveAttribute('href', `${REPO}/v1.6.2/CHANGELOG.md`);
  });

  it('does not invent a release moment for a local build', () => {
    render(
      <AboutPage
        build={{ version: '1.6.2-local-20261002-220000Z', official: false, releaseDate: null, sourceRef: 'main' }}
      />,
    );

    expect(screen.getByText('1.6.2-local-20261002-220000Z')).toBeInTheDocument();
    expect(screen.getByText('Laatste release').nextElementSibling).toHaveTextContent(
      'Niet beschikbaar: dit is een lokale build, geen uitgebrachte release.',
    );
    expect(screen.getByRole('link', { name: /^Changelog bekijken\s+\(opent in een nieuw tabblad\)$/ })).toHaveAttribute('href', `${REPO}/main/CHANGELOG.md`);
  });

  it('says so when an official image carries no release moment', () => {
    render(<AboutPage build={{ version: '1.6.2', official: true, releaseDate: null, sourceRef: 'v1.6.2' }} />);

    expect(screen.getByText('Laatste release').nextElementSibling).toHaveTextContent('Onbekend voor deze build.');
  });
});

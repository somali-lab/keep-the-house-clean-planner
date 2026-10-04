import { screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { applyLanguage } from '../../i18n/runtime.ts';
import { ANNA, makeBadge, makeBadgeImage, mockApi, page, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { MyBadges, PersonBadges } from './PersonBadges.tsx';

const TOILET = makeBadge({ id: 'b00000000000000000000001', name: 'Toiletjuffrouw', description: 'Het toilet vaak gedaan', image: makeBadgeImage('b00000000000000000000001') });
const MOP = makeBadge({ id: 'b00000000000000000000002', name: 'Dweilkampioen', rule: { type: 'minutes', taskIds: [], threshold: 300 } });
const OFF = makeBadge({ id: 'b00000000000000000000003', name: 'Uitgeschakeld', active: false });

function setup(items: { badgeId: string; current: number; threshold: number; awardedAt: string | null }[], badges = [TOILET, MOP, OFF]) {
  storeProfile(ANNA.id);
  return mockApi({
    '/api/v2/users': page([ANNA]),
    '/api/v2/settings': makeSettings(),
    '/api/v2/badges': page(badges),
    '/api/v2/badges/progress': { personId: ANNA.id, items },
  });
}

describe('PersonBadges', () => {
  it('shows an earned badge with its picture, name and the day it was earned, and an open one with its progress as text', async () => {
    const fetchMock = setup([
      { badgeId: TOILET.id, current: 10, threshold: 10, awardedAt: '2026-09-16T22:30:00.000Z' },
      { badgeId: MOP.id, current: 120, threshold: 300, awardedAt: null },
    ]);
    renderWithProviders(<PersonBadges personId={ANNA.id} title="Badges van Anna" />);

    const section = await screen.findByRole('region', { name: 'Badges van Anna' });
    expect(within(section).getByText('1 behaald')).toBeInTheDocument();
    const items = within(section).getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(fetchMock.mock.calls.map(([url]) => url)).toContain(`/api/v2/badges/progress?personId=${ANNA.id}`);

    // The earned badge comes first: the picture has the badge name as its alternative text, and the state is written out.
    const picture = within(items[0]!).getByRole('img', { name: 'Toiletjuffrouw' });
    expect(picture).toHaveAttribute('src', TOILET.image!.url);
    expect(within(items[0]!).getByText('Het toilet vaak gedaan')).toBeInTheDocument();
    // 16 September 22:30 UTC is already 17 September in Amsterdam.
    expect(within(items[0]!).getByText('Behaald op 17 september 2026')).toBeInTheDocument();
    expect(items[0]).not.toHaveTextContent('Nog niet behaald');

    // The open badge has no uploaded image, so the standard medal is named like the badge; the progress is text, not only a dimmed picture.
    expect(within(items[1]!).getByRole('img', { name: 'Dweilkampioen' })).not.toHaveAttribute('src');
    expect(items[1]).toHaveTextContent('Nog niet behaald');
    expect(within(items[1]!).getByText('120/300')).toBeInTheDocument();
    expect(within(items[1]!).getByText('Voortgang 120 van 300')).toBeInTheDocument();
    expect(items[1]).not.toHaveTextContent('Behaald op');
  });

  it('never shows progress above the threshold, and leaves inactive badges out', async () => {
    setup([
      { badgeId: TOILET.id, current: 14, threshold: 10, awardedAt: null },
      { badgeId: MOP.id, current: 0, threshold: 300, awardedAt: null },
    ]);
    renderWithProviders(<PersonBadges personId={ANNA.id} title="Badges van Anna" />);
    const section = await screen.findByRole('region', { name: 'Badges van Anna' });
    expect(within(section).getByText('10/10')).toBeInTheDocument();
    expect(within(section).queryByText('Uitgeschakeld')).not.toBeInTheDocument();
    expect(within(section).queryByRole('img', { name: 'Uitgeschakeld' })).not.toBeInTheDocument();
  });

  it('writes the dates and states in English when the language is English', async () => {
    applyLanguage('en');
    setup([{ badgeId: TOILET.id, current: 10, threshold: 10, awardedAt: '2026-09-16T08:00:00.000Z' }, { badgeId: MOP.id, current: 5, threshold: 300, awardedAt: null }]);
    renderWithProviders(<PersonBadges personId={ANNA.id} title="Badges of Anna" />);
    const section = await screen.findByRole('region', { name: 'Badges of Anna' });
    expect(within(section).getByText(/^Earned on 16 September 2026$/)).toBeInTheDocument();
    expect(within(section).getByText('Not earned yet', { exact: false })).toBeInTheDocument();
    expect(within(section).getByText('Progress 5 of 300')).toBeInTheDocument();
  });

  it('renders nothing when there are no active badges, and nothing when the badges cannot be read', async () => {
    const fetchMock = setup([], [OFF]);
    const { container } = renderWithProviders(<PersonBadges personId={ANNA.id} title="Badges van Anna" />);
    await waitFor(() => expect(fetchMock.mock.calls.some(([url]) => String(url).startsWith('/api/v2/badges/progress'))).toBe(true));
    await waitFor(() => expect(container).toBeEmptyDOMElement());

    storeProfile(ANNA.id);
    mockApi({ '/api/v2/users': page([ANNA]), '/api/v2/settings': makeSettings() });
    const failing = renderWithProviders(<PersonBadges personId={ANNA.id} title="Badges van Anna" />);
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(failing.container).toBeEmptyDOMElement();
  });

  it('is the small "Mijn badges" section of the active profile', async () => {
    setup([{ badgeId: TOILET.id, current: 1, threshold: 10, awardedAt: null }]);
    renderWithProviders(<MyBadges personId={ANNA.id} />);
    const section = await screen.findByRole('region', { name: 'Mijn badges' });
    expect(within(section).getByRole('heading', { level: 2, name: 'Mijn badges' })).toBeInTheDocument();
    expect(within(section).getByText('1/10')).toBeInTheDocument();
  });
});

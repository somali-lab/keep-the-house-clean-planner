import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ANNA, BRAM, makeUser, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { BrowserNotificationsSection } from './BrowserNotificationsSection.tsx';

interface Shown {
  title: string;
  body?: string;
}
let shown: Shown[];
let permission: NotificationPermission;
let requestResult: NotificationPermission;
let refuse: boolean;

function stubNotification() {
  shown = [];
  class FakeNotification {
    static get permission() {
      return permission;
    }
    static requestPermission = vi.fn(async () => {
      permission = requestResult;
      return permission;
    });
    constructor(title: string, options?: NotificationOptions) {
      if (refuse) throw new Error('refused');
      shown.push({ title, ...(options?.body ? { body: options.body } : {}) });
    }
  }
  vi.stubGlobal('Notification', FakeNotification);
  return FakeNotification;
}

const member = makeUser({ ...BRAM, browserNotifications: { enabled: true, times: ['18:30', '08:00'] } });

function setup(profile = member) {
  storeProfile(profile._id);
  return mockApi({
    '/api/users': profile._id === ANNA._id ? [ANNA, BRAM] : [ANNA, profile],
    '/api/settings': makeSettings(),
    [`PUT /api/users/${BRAM._id}/browser-notifications`]: (init: RequestInit) => ({ ...member, browserNotifications: JSON.parse(String(init.body)) }),
    [`PUT /api/users/${ANNA._id}/browser-notifications`]: (init: RequestInit) => ({ ...ANNA, browserNotifications: JSON.parse(String(init.body)) }),
  });
}

const puts = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls
    .filter(([, init]) => (init as RequestInit | undefined)?.method === 'PUT')
    .map(([url, init]) => [url, JSON.parse(String((init as RequestInit).body))]);

beforeEach(() => {
  permission = 'default';
  requestResult = 'granted';
  refuse = false;
});

describe('BrowserNotificationsSection moments', () => {
  beforeEach(() => void stubNotification());

  it("shows the person's own moments sorted, in the household timezone, without a person picker", async () => {
    setup();
    renderWithProviders(<BrowserNotificationsSection />);
    const list = await screen.findByRole('list', { name: 'Gekozen tijden' });
    expect(within(list).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['08:00', '18:30']);
    expect(screen.getByLabelText('Browsermeldingen aan')).toBeChecked();
    expect(screen.getByText(/Europe\/Amsterdam/)).toBeInTheDocument();
    expect(screen.queryByLabelText('Instellen voor')).not.toBeInTheDocument();
  });

  it('adds and removes times and saves them with the switch', async () => {
    const fetchMock = setup(makeUser({ ...BRAM, browserNotifications: { enabled: false, times: [] } }));
    renderWithProviders(<BrowserNotificationsSection />);
    expect(await screen.findByText('Nog geen tijden ingesteld.')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Nieuwe tijd'), { target: { value: '18:30' } });
    fireEvent.click(screen.getByRole('button', { name: 'Tijd toevoegen' }));
    fireEvent.change(screen.getByLabelText('Nieuwe tijd'), { target: { value: '07:15' } });
    fireEvent.click(screen.getByRole('button', { name: 'Tijd toevoegen' }));
    fireEvent.change(screen.getByLabelText('Nieuwe tijd'), { target: { value: '12:00' } });
    fireEvent.click(screen.getByRole('button', { name: 'Tijd toevoegen' }));
    fireEvent.click(screen.getByRole('button', { name: 'Verwijder 12:00' }));
    fireEvent.click(screen.getByLabelText('Browsermeldingen aan'));
    fireEvent.click(screen.getByRole('button', { name: 'Opslaan' }));

    await waitFor(() =>
      expect(puts(fetchMock)).toEqual([
        [`/api/users/${BRAM._id}/browser-notifications`, { enabled: true, times: ['07:15', '18:30'] }],
      ]),
    );
    expect(await screen.findByRole('status')).toHaveTextContent('Opgeslagen.');
  });

  it('explains an empty, duplicate or surplus time without saving', async () => {
    const fetchMock = setup(makeUser({ ...BRAM, browserNotifications: { enabled: true, times: ['08:00'] } }));
    renderWithProviders(<BrowserNotificationsSection />);
    await screen.findByRole('list', { name: 'Gekozen tijden' });
    const add = screen.getByRole('button', { name: 'Tijd toevoegen' });

    fireEvent.click(add);
    expect(screen.getByRole('alert')).toHaveTextContent('Vul een tijd in als uu:mm.');

    fireEvent.change(screen.getByLabelText('Nieuwe tijd'), { target: { value: '08:00' } });
    fireEvent.click(add);
    expect(screen.getByRole('alert')).toHaveTextContent('Deze tijd staat al in de lijst.');

    for (const time of ['09:00', '10:00', '11:00', '12:00', '13:00']) {
      fireEvent.change(screen.getByLabelText('Nieuwe tijd'), { target: { value: time } });
      fireEvent.click(add);
    }
    expect(within(screen.getByRole('list', { name: 'Gekozen tijden' })).getAllByRole('listitem')).toHaveLength(6);
    expect(add).toBeDisabled();
    expect(puts(fetchMock)).toEqual([]);
  });

  it('adds a time with Enter instead of saving the form', async () => {
    const fetchMock = setup(makeUser({ ...BRAM, browserNotifications: { enabled: true, times: [] } }));
    renderWithProviders(<BrowserNotificationsSection />);
    const input = await screen.findByLabelText('Nieuwe tijd');
    fireEvent.change(input, { target: { value: '07:45' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    const list = await screen.findByRole('list', { name: 'Gekozen tijden' });
    expect(within(list).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['07:45']);
    expect(input).toHaveValue('');
    expect(puts(fetchMock)).toEqual([]);
  });

  it('saves a time that was typed but not added yet', async () => {
    const fetchMock = setup(makeUser({ ...BRAM, browserNotifications: { enabled: true, times: ['08:00'] } }));
    renderWithProviders(<BrowserNotificationsSection />);
    fireEvent.change(await screen.findByLabelText('Nieuwe tijd'), { target: { value: '07:00' } });
    fireEvent.click(screen.getByRole('button', { name: 'Opslaan' }));
    await waitFor(() =>
      expect(puts(fetchMock)).toEqual([[`/api/users/${BRAM._id}/browser-notifications`, { enabled: true, times: ['07:00', '08:00'] }]]),
    );
    expect(within(screen.getByRole('list', { name: 'Gekozen tijden' })).getAllByRole('listitem')).toHaveLength(2);
  });

  it('does not save while the pending time is invalid, and links the error to the input', async () => {
    const fetchMock = setup(makeUser({ ...BRAM, browserNotifications: { enabled: true, times: ['08:00'] } }));
    renderWithProviders(<BrowserNotificationsSection />);
    const input = await screen.findByLabelText('Nieuwe tijd');
    fireEvent.change(input, { target: { value: '08:00' } });
    fireEvent.click(screen.getByRole('button', { name: 'Opslaan' }));
    const alert = screen.getByRole('alert');
    expect(alert).toHaveTextContent('Deze tijd staat al in de lijst.');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    const describedBy = input.getAttribute('aria-describedby')!;
    expect(document.getElementById(describedBy)).toContainElement(alert);
    expect(puts(fetchMock)).toEqual([]);
    fireEvent.change(input, { target: { value: '09:00' } });
    expect(input).not.toHaveAttribute('aria-describedby');
  });

  it('clears the saved message as soon as something is edited', async () => {
    setup();
    renderWithProviders(<BrowserNotificationsSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Opslaan' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Opgeslagen.');
    fireEvent.click(screen.getByLabelText('Browsermeldingen aan'));
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it("says whose times were saved when an administrator edits another person", async () => {
    setup(ANNA);
    renderWithProviders(<BrowserNotificationsSection />);
    fireEvent.change(await screen.findByLabelText('Instellen voor'), { target: { value: BRAM._id } });
    fireEvent.click(screen.getByRole('button', { name: 'Opslaan' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Opgeslagen voor Bram de Vries.');
  });

  it('lets an administrator pick another person and saves for that person', async () => {
    const fetchMock = setup(ANNA);
    renderWithProviders(<BrowserNotificationsSection />);
    const picker = await screen.findByLabelText('Instellen voor');
    expect(within(picker).getAllByRole('option').map((o) => o.textContent)).toEqual(['Anna', 'Bram de Vries']);
    expect(picker).toHaveValue(ANNA._id);

    fireEvent.change(picker, { target: { value: BRAM._id } });
    fireEvent.change(screen.getByLabelText('Nieuwe tijd'), { target: { value: '09:00' } });
    fireEvent.click(screen.getByRole('button', { name: 'Tijd toevoegen' }));
    fireEvent.click(screen.getByRole('button', { name: 'Opslaan' }));

    await waitFor(() => expect(puts(fetchMock)).toHaveLength(1));
    expect(puts(fetchMock)[0]).toEqual([`/api/users/${BRAM._id}/browser-notifications`, { enabled: false, times: ['09:00'] }]);
  });
});

describe('BrowserNotificationsSection device permission', () => {
  const status = async () => (await screen.findByRole('region', { name: 'Toestemming op dit apparaat' }));

  it('says clearly when the browser does not support notifications', async () => {
    setup();
    renderWithProviders(<BrowserNotificationsSection />);
    const card = await status();
    expect(within(card).getByText('Status: niet ondersteund')).toBeInTheDocument();
    expect(within(card).getByText(/Deze browser ondersteunt geen meldingen/)).toBeInTheDocument();
    expect(within(card).getByRole('button', { name: 'Toestemming vragen' })).toBeDisabled();
    expect(within(card).getByRole('button', { name: 'Testmelding sturen' })).toBeDisabled();
  });

  it('asks for permission from a button and then offers the test notification', async () => {
    const notification = stubNotification();
    setup();
    renderWithProviders(<BrowserNotificationsSection />);
    const card = await status();
    expect(within(card).getByText('Status: nog niet gevraagd')).toBeInTheDocument();
    expect(within(card).getByRole('button', { name: 'Testmelding sturen' })).toBeDisabled();
    expect(notification.requestPermission).not.toHaveBeenCalled();

    fireEvent.click(within(card).getByRole('button', { name: 'Toestemming vragen' }));
    expect(await within(card).findByText('Status: toegestaan')).toBeInTheDocument();
    expect(notification.requestPermission).toHaveBeenCalledTimes(1);
    expect(within(card).getByRole('button', { name: 'Toestemming vragen' })).toBeDisabled();

    fireEvent.click(within(card).getByRole('button', { name: 'Testmelding sturen' }));
    expect(await within(card).findByRole('status')).toHaveTextContent('Testmelding verstuurd.');
    expect(shown).toEqual([{ title: 'Testmelding', body: 'Zo ziet een melding van Keep the House Clean er uit.' }]);
  });

  it('shows a blocked permission with a way out and no actions', async () => {
    permission = 'denied';
    stubNotification();
    setup();
    renderWithProviders(<BrowserNotificationsSection />);
    const card = await status();
    expect(within(card).getByText('Status: geblokkeerd')).toBeInTheDocument();
    expect(within(card).getByText(/Sta ze toe via de site-instellingen van de browser/)).toBeInTheDocument();
    expect(within(card).getByRole('button', { name: 'Toestemming vragen' })).toBeDisabled();
    expect(within(card).getByRole('button', { name: 'Testmelding sturen' })).toBeDisabled();
  });

  it('explains that a plain-HTTP address cannot show notifications and disables both buttons', async () => {
    permission = 'granted';
    stubNotification();
    Object.defineProperty(window, 'isSecureContext', { configurable: true, value: false });
    try {
      setup();
      renderWithProviders(<BrowserNotificationsSection />);
      const card = await status();
      expect(within(card).getByText('Status: niet beveiligd (geen HTTPS)')).toBeInTheDocument();
      expect(within(card).getByText(/http:\/\/192\.168\.x\.x:3000/)).toBeInTheDocument();
      expect(within(card).getByRole('button', { name: 'Toestemming vragen' })).toBeDisabled();
      expect(within(card).getByRole('button', { name: 'Testmelding sturen' })).toBeDisabled();
    } finally {
      Reflect.deleteProperty(window, 'isSecureContext');
    }
  });

  it('reports a test notification that the browser refused', async () => {
    permission = 'granted';
    stubNotification();
    refuse = true;
    setup();
    renderWithProviders(<BrowserNotificationsSection />);
    const card = await status();
    fireEvent.click(within(card).getByRole('button', { name: 'Testmelding sturen' }));
    expect(await within(card).findByRole('alert')).toHaveTextContent('De testmelding kon niet worden getoond.');
  });
});

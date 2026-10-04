import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { resetRequestKeys } from '../../api/requestKey.ts';
import { ANNA, BRAM, LIMITS, mockApi, problem, storeProfile, v2Basics } from '../../test/fixtures.ts';
import { renderWithProviders } from '../../test/render.tsx';
import type { PointEntry, PointsBalances } from './api.ts';
import { RedeemDialog } from './RedeemDialog.tsx';

afterEach(() => resetRequestKeys());
const KEY = /^[A-Za-z0-9_-]{16,64}$/;

/** A balance; the money is in cents, as the server delivers it, and absent while a point is worth nothing. */
const balance = (personId: string, points: number, centsPerPoint: number, redeemed = 0) => ({
  personId,
  points,
  earned: points + redeemed,
  redeemed,
  money:
    centsPerPoint > 0
      ? { earned: (points + redeemed) * centsPerPoint, redeemed: redeemed * centsPerPoint, balance: points * centsPerPoint }
      : null,
  executions: 1,
  bonusPoints: 0,
});

const BALANCES = (centsPerPoint: number, annaPoints = 10): PointsBalances => ({
  from: null,
  to: null,
  currencyCode: 'EUR',
  centsPerPoint,
  balances: [balance(ANNA._id, annaPoints, centsPerPoint), balance(BRAM._id, 4, centsPerPoint)],
});

const entryFor = (personId: string, points: number, note: string | null, cents: number): PointEntry => ({
  id: 'e00000000000000000000009',
  key: 'redemption:e00000000000000000000009',
  kind: 'redemption',
  personId,
  amount: -points,
  date: '2026-09-16',
  weekStart: '2026-09-14',
  periodStart: null,
  occurrenceId: null,
  taskId: null,
  titleSnapshot: '',
  note,
  centsPerPointSnapshot: cents,
  currencyCodeSnapshot: 'EUR',
  source: 'live',
  createdAt: '2026-09-16T08:00:00.000Z',
  updatedAt: '2026-09-16T08:00:00.000Z',
});

/** 201: a new booking; a plain value answers 200, which means the server replayed an earlier request. */
const created = (entry: PointEntry) => new Response(JSON.stringify(entry), { status: 201, headers: { 'Content-Type': 'application/json' } });

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

function setup(profileId: string, routes: Record<string, unknown> = {}, balances = BALANCES(25)) {
  storeProfile(profileId);
  const fetchMock = mockApi({
    '/api/users': [ANNA, BRAM],
    '/api/v2/points/balances': balances,
    ...v2Basics(),
    ...routes,
  });
  const onOpenChange = vi.fn();
  const onRedeemed = vi.fn();
  const view = renderWithProviders(<RedeemDialog open onOpenChange={onOpenChange} onRedeemed={onRedeemed} />);
  return { fetchMock, onOpenChange, onRedeemed, view };
}

const bookings = (fetchMock: ReturnType<typeof mockApi>) =>
  fetchMock.mock.calls.filter(([u, init]) => u === '/api/v2/points/redemptions' && (init as RequestInit | undefined)?.method === 'POST');

/** The JSON bodies of the bookings, exactly as sent. */
const posts = (fetchMock: ReturnType<typeof mockApi>) =>
  bookings(fetchMock).map(([, init]) => JSON.parse(String((init as RequestInit).body)) as Record<string, unknown>);

const dialog = () => screen.getByRole('dialog', { name: 'Punten inwisselen' });
const submit = () => within(dialog()).getByRole('button', { name: 'Inwisselen' });
const points = () => within(dialog()).getByLabelText('Aantal punten');
const loaded = async () => screen.findByText(/Beschikbaar saldo/);

describe('RedeemDialog', () => {
  it('shows the available balance with what it is worth, from the money the server delivers, and previews the money while typing', async () => {
    setup(ANNA._id);
    expect(await loaded()).toHaveTextContent(/Beschikbaar saldo: 10 punten \(€\s2,50\)/);
    expect(screen.queryByText(/Dat is/)).not.toBeInTheDocument();
    fireEvent.change(points(), { target: { value: '4' } });
    expect(screen.getByText(/Dat is €\s1,00\./)).toBeInTheDocument();
    fireEvent.change(points(), { target: { value: '7' } });
    expect(screen.getByText(/Dat is €\s1,75\./)).toBeInTheDocument();
    // An invalid number previews nothing.
    fireEvent.change(points(), { target: { value: '1.5' } });
    expect(screen.queryByText(/Dat is/)).not.toBeInTheDocument();
  });

  it('shows no money while a point is worth nothing', async () => {
    setup(ANNA._id, {}, BALANCES(0));
    expect(await loaded()).toHaveTextContent('Beschikbaar saldo: 10 punten');
    expect(screen.getByText(/Beschikbaar saldo/)).not.toHaveTextContent('€');
    fireEvent.change(points(), { target: { value: '4' } });
    expect(screen.queryByText(/Dat is/)).not.toBeInTheDocument();
  });

  it('refuses a missing, fractional, negative or too large amount and a long note, without calling the server', async () => {
    const { fetchMock } = setup(ANNA._id);
    await loaded();
    for (const bad of ['', '0', '-1', '1.5', 'abc']) {
      fireEvent.change(points(), { target: { value: bad } });
      fireEvent.click(submit());
      expect(await within(dialog()).findByText('Vul een heel aantal punten in, minimaal 1.')).toBeInTheDocument();
      expect(points()).toHaveAttribute('aria-invalid', 'true');
      expect(points()).toHaveFocus();
    }
    fireEvent.change(points(), { target: { value: '11' } });
    fireEvent.click(submit());
    expect(await within(dialog()).findByText('Dat is meer dan het beschikbare saldo (10 punten).')).toBeInTheDocument();
    expect(within(dialog()).getByRole('alert')).toHaveTextContent('Controleer de gemarkeerde velden.');

    fireEvent.change(points(), { target: { value: '5' } });
    fireEvent.change(within(dialog()).getByLabelText('Notitie (optioneel)'), { target: { value: 'x'.repeat(201) } });
    fireEvent.click(submit());
    expect(await within(dialog()).findByText('De notitie mag maximaal 200 tekens zijn.')).toBeInTheDocument();
    expect(posts(fetchMock)).toEqual([]);
  });

  it('takes the length of the note from the limits of the server', async () => {
    const { fetchMock } = setup(ANNA._id, { '/api/v2/meta/limits': { ...LIMITS, points: { ...LIMITS.points, maxRedemptionNoteLength: 20 } } });
    await loaded();
    fireEvent.change(points(), { target: { value: '5' } });
    fireEvent.change(within(dialog()).getByLabelText('Notitie (optioneel)'), { target: { value: 'x'.repeat(21) } });
    fireEvent.click(submit());
    expect(await within(dialog()).findByText('De notitie mag maximaal 20 tekens zijn.')).toBeInTheDocument();
    expect(posts(fetchMock)).toEqual([]);
  });

  it('books for the active profile with a trimmed note and a request key, as plain JSON, reports the money and closes', async () => {
    const { fetchMock, onRedeemed, onOpenChange } = setup(ANNA._id, {
      'POST /api/v2/points/redemptions': () => created(entryFor(ANNA._id, 4, 'Pizza', 25)),
    });
    await loaded();
    fireEvent.change(points(), { target: { value: '4' } });
    fireEvent.change(within(dialog()).getByLabelText('Notitie (optioneel)'), { target: { value: '  Pizza  ' } });
    fireEvent.click(submit());
    await waitFor(() => expect(onRedeemed).toHaveBeenCalledTimes(1));
    // The points go as a number; an intent endpoint carries no If-Match.
    expect(posts(fetchMock)).toEqual([{ personId: ANNA._id, points: 4, note: 'Pizza', requestId: expect.stringMatching(KEY) }]);
    expect(((bookings(fetchMock)[0]![1] as RequestInit).headers as Record<string, string>)['if-match']).toBeUndefined();
    expect(onRedeemed.mock.calls[0]![0]).toMatchObject({ amount: -4 });
    expect(onRedeemed.mock.calls[0]![1]).toMatch(/€\s1,00/);
    expect(onRedeemed.mock.calls[0]![2]).toBe(false);
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it('leaves the note out, never as null, when it is empty and reports no money while a point is worth nothing', async () => {
    const { fetchMock, onRedeemed } = setup(
      ANNA._id,
      { 'POST /api/v2/points/redemptions': () => entryFor(ANNA._id, 3, null, 0) },
      BALANCES(0),
    );
    await loaded();
    fireEvent.change(points(), { target: { value: '3' } });
    fireEvent.click(submit());
    await waitFor(() => expect(onRedeemed).toHaveBeenCalled());
    expect(posts(fetchMock)[0]).toEqual({ personId: ANNA._id, points: 3, requestId: expect.stringMatching(KEY) });
    expect(onRedeemed.mock.calls[0]![1]).toBeNull();
  });

  it('tells the caller when the server replayed a booking it already had (200 instead of 201)', async () => {
    const { onRedeemed } = setup(ANNA._id, { 'POST /api/v2/points/redemptions': () => entryFor(ANNA._id, 2, null, 25) });
    await loaded();
    fireEvent.change(points(), { target: { value: '2' } });
    fireEvent.click(submit());
    await waitFor(() => expect(onRedeemed).toHaveBeenCalledTimes(1));
    expect(onRedeemed.mock.calls[0]![2]).toBe(true);
  });

  it('a double click books once: the second click is ignored while the first is pending', async () => {
    const reply = deferred<PointEntry>();
    const { fetchMock, onRedeemed } = setup(ANNA._id, { 'POST /api/v2/points/redemptions': () => reply.promise });
    await loaded();
    fireEvent.change(points(), { target: { value: '2' } });
    const button = submit();
    fireEvent.click(button);
    fireEvent.click(button);
    expect(await screen.findByRole('button', { name: 'Bezig met inwisselen…' })).toBeDisabled();
    expect(posts(fetchMock)).toHaveLength(1);
    reply.resolve(entryFor(ANNA._id, 2, null, 25));
    await waitFor(() => expect(onRedeemed).toHaveBeenCalledTimes(1));
    expect(posts(fetchMock)).toHaveLength(1);
  });

  it('reuses the request key when the same values are retried after a failure, and takes a new one once it succeeded', async () => {
    let attempts = 0;
    const { fetchMock, onRedeemed, view } = setup(ANNA._id, {
      'POST /api/v2/points/redemptions': () => {
        attempts += 1;
        if (attempts === 1) throw new TypeError('network down');
        return entryFor(ANNA._id, 2, null, 25);
      },
    });
    await loaded();
    fireEvent.change(points(), { target: { value: '2' } });
    fireEvent.click(submit());
    expect(await screen.findByText('Het inwisselen is niet gelukt. Probeer het opnieuw.')).toBeInTheDocument();
    await waitFor(() => expect(submit()).toBeEnabled());
    fireEvent.click(submit());
    await waitFor(() => expect(onRedeemed).toHaveBeenCalledTimes(1));
    const [first, second] = posts(fetchMock) as { requestId: string }[];
    expect(second!.requestId).toBe(first!.requestId);

    // The same values again are a new intent once the first booking succeeded.
    view.unmount();
    renderWithProviders(<RedeemDialog open onOpenChange={vi.fn()} onRedeemed={onRedeemed} />);
    await loaded();
    fireEvent.change(points(), { target: { value: '2' } });
    fireEvent.click(submit());
    await waitFor(() => expect(onRedeemed).toHaveBeenCalledTimes(2));
    expect((posts(fetchMock)[2] as { requestId: string }).requestId).not.toBe(first!.requestId);
  });

  it('says so when the server finds the balance too low in the meantime, and keeps the dialog open', async () => {
    const { onOpenChange } = setup(ANNA._id, {
      'POST /api/v2/points/redemptions': () => problem(409, 'insufficient_balance', 'The balance is too low', { balance: 1, requested: 4 }),
    });
    await loaded();
    fireEvent.change(points(), { target: { value: '4' } });
    fireEvent.click(submit());
    expect(await within(dialog()).findByRole('alert')).toHaveTextContent('Het saldo is inmiddels te laag.');
    expect(onOpenChange).not.toHaveBeenCalledWith(false);
  });

  it('lets a member redeem only for themselves: no person choice', async () => {
    const { fetchMock, onRedeemed } = setup(BRAM._id, { 'POST /api/v2/points/redemptions': () => entryFor(BRAM._id, 3, null, 25) });
    expect(await loaded()).toHaveTextContent('Beschikbaar saldo: 4 punten');
    expect(within(dialog()).queryByLabelText('Persoon')).not.toBeInTheDocument();
    fireEvent.change(points(), { target: { value: '3' } });
    fireEvent.click(submit());
    await waitFor(() => expect(onRedeemed).toHaveBeenCalled());
    expect(posts(fetchMock)[0]).toMatchObject({ personId: BRAM._id });
  });

  it('lets an administrator pick the person, whose balance applies', async () => {
    const { fetchMock, onRedeemed } = setup(ANNA._id, { 'POST /api/v2/points/redemptions': () => entryFor(BRAM._id, 3, null, 25) });
    await loaded();
    const person = within(dialog()).getByLabelText('Persoon');
    expect(person).toHaveValue(ANNA._id);
    fireEvent.change(person, { target: { value: BRAM._id } });
    expect(await screen.findByText(/Beschikbaar saldo: 4 punten/)).toBeInTheDocument();
    fireEvent.change(points(), { target: { value: '5' } });
    fireEvent.click(submit());
    expect(await within(dialog()).findByText('Dat is meer dan het beschikbare saldo (4 punten).')).toBeInTheDocument();
    fireEvent.change(points(), { target: { value: '3' } });
    fireEvent.click(submit());
    await waitFor(() => expect(onRedeemed).toHaveBeenCalled());
    expect(posts(fetchMock)[0]).toMatchObject({ personId: BRAM._id, points: 3 });
  });

  it('cannot book without points: the button is disabled and it says why', async () => {
    const { fetchMock } = setup(ANNA._id, {}, BALANCES(25, 0));
    expect(await screen.findByText('Er zijn geen punten om in te wisselen.')).toBeInTheDocument();
    expect(submit()).toBeDisabled();
    expect(posts(fetchMock)).toEqual([]);
  });
});

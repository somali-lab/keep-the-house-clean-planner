import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeOccurrence, makeSettings } from '../../test/render.tsx';
import { renderWithProviders } from '../../test/render.tsx';
import { CompletionManagementPage } from './CompletionManagementPage.tsx';

describe('CompletionManagementPage', () => {
  it('edits and permanently deletes completed occurrences after confirmation', async () => {
    storeProfile(ANNA._id);
    let records = [
      makeOccurrence({
        _id: 'o00000000000000000000001',
        taskNameSnapshot: 'Kattenmandjes',
        roomNameSnapshot: 'Hobbykamer Sven',
        date: '2026-09-27',
        status: 'done',
        statusBeforeCompletion: 'open',
        completedAt: '2026-09-27T08:00:00.000Z',
        completedBy: ANNA._id,
      }),
      makeOccurrence({
        _id: 'o00000000000000000000002',
        taskNameSnapshot: 'Badkamer',
        date: '2026-09-26',
        status: 'done',
        statusBeforeCompletion: 'open',
        completedAt: '2026-09-26T09:00:00.000Z',
        completedBy: BRAM._id,
      }),
    ];
    const fetchMock = mockApi({
      '/api/users': [ANNA, BRAM],
      '/api/settings': makeSettings(),
      '/api/occurrences': () => records,
      'PATCH /api/occurrences/o00000000000000000000001': (init: RequestInit | undefined) => {
        const body = JSON.parse(String(init?.body));
        records = records.map((record) => record._id === 'o00000000000000000000001'
          ? { ...record, date: body.date, completedAt: body.completedAt, completedBy: body.completedBy }
          : record);
        return records[0];
      },
      'DELETE /api/occurrences/o00000000000000000000001': () => {
        records = records.filter((record) => record._id !== 'o00000000000000000000001');
        return { deleted: true };
      },
    });

    renderWithProviders(<CompletionManagementPage now={new Date('2026-09-27T12:00:00.000Z')} />);
    expect(await screen.findByRole('heading', { name: 'Gereedmeldingen beheren' })).toBeInTheDocument();
    const row = (await screen.findByText('Kattenmandjes')).closest('li')!;
    fireEvent.click(within(row).getByRole('button', { name: 'Kattenmandjes bewerken' }));

    const editDialog = await screen.findByRole('dialog', { name: 'Kattenmandjes bewerken' });
    fireEvent.change(within(editDialog).getByLabelText('Taakdatum'), { target: { value: '2026-09-28' } });
    fireEvent.change(within(editDialog).getByLabelText('Gereed op'), { target: { value: '2026-09-28T11:30' } });
    fireEvent.change(within(editDialog).getByLabelText('Uitgevoerd door'), { target: { value: BRAM._id } });
    fireEvent.click(within(editDialog).getByRole('button', { name: 'Opslaan' }));

    await waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => {
      if (url !== '/api/occurrences/o00000000000000000000001' || (init as RequestInit).method !== 'PATCH') return false;
      const body = JSON.parse(String((init as RequestInit).body));
      return body.action === 'edit_completion' && body.date === '2026-09-28' && body.completedBy === BRAM._id;
    })).toBe(true));
    expect(await screen.findByRole('status')).toHaveTextContent('De gereedmelding is bijgewerkt.');

    const updatedRow = (await screen.findByText('Kattenmandjes')).closest('li')!;
    fireEvent.click(within(updatedRow).getByRole('button', { name: 'Gereedmelding van Kattenmandjes verwijderen' }));
    const deleteDialog = await screen.findByRole('dialog', { name: 'Kattenmandjes verwijderen?' });
    expect(deleteDialog).toHaveTextContent('Ook deze geplande taakinstantie verdwijnt');
    fireEvent.click(within(deleteDialog).getByRole('button', { name: 'Definitief verwijderen' }));

    await waitFor(() => expect(screen.queryByText('Kattenmandjes')).not.toBeInTheDocument());
    expect(fetchMock.mock.calls.some(([url, init]) => url === '/api/occurrences/o00000000000000000000001' && (init as RequestInit).method === 'DELETE')).toBe(true);
  });
});
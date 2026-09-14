import { fireEvent, screen, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ANNA, BRAM, mockApi, storeProfile } from '../../test/fixtures.ts';
import { makeSettings, renderWithProviders } from '../../test/render.tsx';
import { ExportDialog } from './ExportDialog.tsx';

const NOW = new Date('2026-09-16T08:00:00Z'); // Wednesday, 2026-W38
const CYCLES = [
  { _id: 'c0', index: 0, startDate: '2026-09-14', endDate: '2026-10-11', planId: 'p1', generatedAt: '2026-09-14T01:00:00.000Z', generationRunId: 'r' },
  { _id: 'c1', index: 1, startDate: '2026-10-12', endDate: '2026-11-08', planId: 'p1', generatedAt: '2026-09-14T01:00:00.000Z', generationRunId: 'r' },
];

function renderDialog() {
  const onClose = vi.fn();
  renderWithProviders(<ExportDialog onClose={onClose} now={NOW} />);
  return { onClose };
}

const startWeek = () => screen.getByLabelText('Startweek');
const option = (label: string) =>
  within(startWeek()).getAllByRole<HTMLOptionElement>('option').find((o) => o.value === label)!;

describe('ExportDialog', () => {
  beforeEach(() => {
    storeProfile(ANNA._id);
    mockApi({ '/api/users': [ANNA, BRAM], '/api/settings': makeSettings(), '/api/cycles': CYCLES });
  });

  it('disables weeks that are not generated and explains why', async () => {
    renderDialog();
    expect(await screen.findByRole('heading', { name: 'PDF exporteren' })).toBeInTheDocument();
    // the heading shows while cycles load; wait for the form itself
    await screen.findByLabelText('Startweek');

    const options = within(startWeek()).getAllByRole<HTMLOptionElement>('option');
    expect(options).toHaveLength(12);
    expect(options[0]).toHaveTextContent('2026-W38 · 14-09 t/m 20-09');
    expect(option('2026-W45').disabled).toBe(false);
    expect(option('2026-W46').disabled).toBe(true);
    expect(option('2026-W46')).toHaveTextContent('nog niet gegenereerd');
    expect(screen.getByRole('note')).toHaveTextContent('Alleen weken die al gegenereerd zijn kunnen worden geëxporteerd.');
  });

  it('takes the whole range into account for 4 weeks', async () => {
    renderDialog();
    fireEvent.click(await screen.findByLabelText('4 weken (hele cyclus)'));
    expect(option('2026-W42').disabled).toBe(false); // W42–W45
    expect(option('2026-W43').disabled).toBe(true); // would need W46
    expect(screen.getByRole('link', { name: 'Download PDF' })).toHaveAttribute(
      'href',
      '/api/export/pdf?fromWeek=2026-W38&weeks=4&orientation=portrait&totals=false',
    );
  });

  it('offers orientation only for two weeks and builds the download link', async () => {
    renderDialog();
    await screen.findByLabelText('Startweek');
    expect(screen.queryByLabelText('Oriëntatie')).not.toBeInTheDocument();

    fireEvent.click(screen.getByLabelText('2 weken'));
    fireEvent.change(startWeek(), { target: { value: '2026-W39' } });
    fireEvent.change(screen.getByLabelText('Oriëntatie'), { target: { value: 'landscape' } });
    fireEvent.click(screen.getByLabelText('Minuten per dag tonen'));

    const link = screen.getByRole('link', { name: 'Download PDF' });
    expect(link).toHaveAttribute('href', '/api/export/pdf?fromWeek=2026-W39&weeks=2&orientation=landscape&totals=true');
    expect(link).toHaveAttribute('download');
  });

  it('disables the download for a day that is not generated', async () => {
    renderDialog();
    fireEvent.click(await screen.findByLabelText('Eén dag'));
    const date = screen.getByLabelText('Datum');
    expect(date).toHaveValue('2026-09-16');
    expect(screen.getByRole('link', { name: 'Download PDF' })).toHaveAttribute('href', '/api/export/pdf/day?date=2026-09-16');

    fireEvent.change(date, { target: { value: '2026-11-20' } });
    expect(screen.getByRole('button', { name: 'Download PDF' })).toBeDisabled();
    expect(screen.getByRole('alert')).toHaveTextContent('nog niet (volledig) gegenereerd');
  });

  it('exports the due list regardless of generated weeks', async () => {
    renderDialog();
    fireEvent.click(await screen.findByLabelText('Achterstand'));
    expect(screen.queryByLabelText('Startweek')).not.toBeInTheDocument();
    expect(screen.getByText(/aan de beurt of flink achter/)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Download PDF' })).toHaveAttribute('href', '/api/export/pdf/due');
  });

  it('closes', async () => {
    const { onClose } = renderDialog();
    fireEvent.click(await screen.findByRole('button', { name: 'Sluiten' }));
    expect(onClose).toHaveBeenCalled();
  });
});

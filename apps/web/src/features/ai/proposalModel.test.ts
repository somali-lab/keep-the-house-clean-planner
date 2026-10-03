import { describe, expect, it } from 'vitest';
import { describeProposalWarning, rationaleWeekLabel } from './proposalModel.ts';

describe('rationaleWeekLabel', () => {
  it('adds a week label when the sentence has none', () => {
    expect(rationaleWeekLabel('Taken zijn gelijkmatig verdeeld.', 1)).toBe('Week 2:');
  });

  it('adds no label when the sentence already starts with one', () => {
    expect(rationaleWeekLabel('Week 3: ramen lappen.', 2)).toBeNull();
    expect(rationaleWeekLabel('  week4 rustig.', 3)).toBeNull();
  });

  it('only treats a leading "week <number>" as a label', () => {
    expect(rationaleWeekLabel('Dit weekend is rustig.', 0)).toBe('Week 1:');
  });
});

describe('describeProposalWarning', () => {
  it('describes the known warning codes in plain language', () => {
    expect(describeProposalWarning({ code: 'interval_mismatch', placed: 1, required: 4 })).toBe(
      '1 van 4 keer gepland.',
    );
    expect(describeProposalWarning({ code: 'over_budget' })).toBe(
      'Iemand gaat over het totale budget voor werkdagen of weekend.',
    );
    expect(describeProposalWarning({ code: 'daily_over_budget' })).toBe(
      'Op een dag gaat iemand over het ingestelde maximum.',
    );
  });

  it('falls back to the code for an unknown warning', () => {
    expect(describeProposalWarning({ code: 'something_new' })).toBe('something_new');
  });
});

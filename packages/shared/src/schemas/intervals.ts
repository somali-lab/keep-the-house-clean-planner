import { z } from 'zod';

export const intervalSchema = z.object({
  key: z.string().trim().min(1).max(32),
  label: z.string().trim().min(1),
  /** Required slots per 28-day cycle; null = not planned via the grid (due engine only). */
  perCycle: z.number().int().min(1).nullable(),
  periodDays: z.number().int().min(1),
});
export type Interval = z.infer<typeof intervalSchema>;

export const DEFAULT_INTERVALS: Interval[] = [
  { key: 'daily', label: 'Dagelijks', perCycle: 28, periodDays: 1 },
  { key: '3w', label: '3x per week', perCycle: 12, periodDays: 2 },
  { key: '2w', label: '2x per week', perCycle: 8, periodDays: 3 },
  { key: '1w', label: '1x per week', perCycle: 4, periodDays: 7 },
  { key: '2wk', label: '1x per 2 weken', perCycle: 2, periodDays: 14 },
  { key: '4wk', label: '1x per 4 weken', perCycle: 1, periodDays: 28 },
  { key: 'quarter', label: '1x per kwartaal', perCycle: null, periodDays: 91 },
];

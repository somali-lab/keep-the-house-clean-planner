import { useQueryClient } from '@tanstack/react-query';
import { Egg, Save } from 'lucide-react';
import { useId, useState, type FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import type { Settings } from '../../api/v2/household.ts';
import { FALLBACK_LIMITS, useLimits } from '../../api/v2/queries.ts';
import { format, t } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { saveErrorText, useUpdateSettings } from './api.ts';
import { Field, FormActions, FormMessage, SettingsCardHeader, settingsCardClass } from './SettingsCard.tsx';

type Message = { kind: 'status' | 'alert'; text: string } | null;

/** The goal typed in a field: null for an empty field (automatic), a whole number within the limits of the server, or `undefined` when it is not valid. */
export function parseRewardGoal(
  text: string,
  limits: { minGoalPoints: number; maxGoalPoints: number } = FALLBACK_LIMITS.rewards,
): number | null | undefined {
  const trimmed = text.trim();
  if (trimmed === '') return null;
  if (!/^\d{1,7}$/.test(trimmed)) return undefined;
  const value = Number(trimmed);
  return value >= limits.minGoalPoints && value <= limits.maxGoalPoints ? value : undefined;
}

const toText = (goal: number | null | undefined) => (goal === null || goal === undefined ? '' : String(goal));

/**
 * The goals of the reward meter (requirements 4.12), for administrators: points per week and per cycle. An empty field
 * means an automatic goal, the points of the work planned for each person; 0 means no goal for that period.
 */
export function RewardGoalsSection({ settings }: { settings: Settings }) {
  const idPrefix = useId();
  const queryClient = useQueryClient();
  const { profile } = useProfile();
  const limits = useLimits().data?.rewards ?? FALLBACK_LIMITS.rewards;
  const update = useUpdateSettings();
  const [week, setWeek] = useState(toText(settings.rewardGoals.weekPoints));
  const [cycle, setCycle] = useState(toText(settings.rewardGoals.cyclePoints));
  const [message, setMessage] = useState<Message>(null);

  if (profile?.role !== 'admin') return null;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const weekPoints = parseRewardGoal(week, limits);
    const cyclePoints = parseRewardGoal(cycle, limits);
    if (weekPoints === undefined || cyclePoints === undefined) {
      setMessage({ kind: 'alert', text: format('settings.reward.invalid', limits) });
      return;
    }
    setMessage(null);
    update.mutate(
      { settings, patch: { rewardGoals: { weekPoints, cyclePoints } } },
      {
        onSuccess: async () => {
          setMessage({ kind: 'status', text: t('settings.saved') });
          await queryClient.invalidateQueries({ queryKey: ['points'] });
        },
        onError: (error) => setMessage({ kind: 'alert', text: saveErrorText(error) }),
      },
    );
  };

  return (
    <form className={settingsCardClass} onSubmit={submit} noValidate aria-labelledby={`${idPrefix}-title`}>
      <SettingsCardHeader
        icon={<Egg aria-hidden="true" />}
        titleId={`${idPrefix}-title`}
        title={t('settings.reward.title')}
        description={t('settings.reward.explainer')}
      />
      <div className="grid gap-4 sm:grid-cols-2">
        <Field>
          <Label htmlFor={`${idPrefix}-week`}>{t('settings.reward.weekGoal')}</Label>
          <Input
            id={`${idPrefix}-week`}
            type="number"
            inputMode="numeric"
            min={limits.minGoalPoints}
            max={limits.maxGoalPoints}
            step="1"
            className="h-10 bg-card sm:max-w-48"
            placeholder={t('settings.reward.automatic')}
            value={week}
            onChange={(event) => setWeek(event.target.value)}
          />
        </Field>
        <Field>
          <Label htmlFor={`${idPrefix}-cycle`}>{t('settings.reward.cycleGoal')}</Label>
          <Input
            id={`${idPrefix}-cycle`}
            type="number"
            inputMode="numeric"
            min={limits.minGoalPoints}
            max={limits.maxGoalPoints}
            step="1"
            className="h-10 bg-card sm:max-w-48"
            placeholder={t('settings.reward.automatic')}
            value={cycle}
            onChange={(event) => setCycle(event.target.value)}
          />
        </Field>
      </div>
      <p className="text-sm text-muted-foreground">{format('settings.reward.hint', limits)}</p>
      {message && <FormMessage kind={message.kind}>{message.text}</FormMessage>}
      <FormActions>
        <Button type="submit" disabled={update.isPending}>
          <Save aria-hidden="true" />
          {t('settings.reward.save')}
        </Button>
      </FormActions>
    </form>
  );
}

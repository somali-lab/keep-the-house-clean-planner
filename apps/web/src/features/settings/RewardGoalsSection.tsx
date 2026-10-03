import type { RewardGoals, Settings } from '@huishoudplanner/shared';
import { MAX_REWARD_GOAL_POINTS, MIN_REWARD_GOAL_POINTS } from '@huishoudplanner/shared/rewards';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Egg, Save } from 'lucide-react';
import { useId, useState, type FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { api } from '../../api/index.ts';
import { queryKeys } from '../../api/queries.ts';
import { t } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { Field, FormActions, FormMessage, SettingsCardHeader, settingsCardClass } from './SettingsCard.tsx';

type Message = { kind: 'status' | 'alert'; text: string } | null;

/** The goal typed in a field: null for an empty field (automatic), a whole number from 0 to 100000, or `undefined` when it is not valid. */
export function parseRewardGoal(text: string): number | null | undefined {
  const trimmed = text.trim();
  if (trimmed === '') return null;
  if (!/^\d{1,6}$/.test(trimmed)) return undefined;
  const value = Number(trimmed);
  return value >= MIN_REWARD_GOAL_POINTS && value <= MAX_REWARD_GOAL_POINTS ? value : undefined;
}

const toText = (goal: number | null | undefined) => (goal === null || goal === undefined ? '' : String(goal));

/**
 * The goals of the reward meter (ADR-0015), for administrators: points per week and per cycle. An empty field
 * means an automatic goal, the points of the work planned for each person; 0 means no goal for that period.
 */
export function RewardGoalsSection({ settings }: { settings: Settings }) {
  const idPrefix = useId();
  const queryClient = useQueryClient();
  const { profile } = useProfile();
  const [week, setWeek] = useState(toText(settings.rewardGoals?.weekPoints));
  const [cycle, setCycle] = useState(toText(settings.rewardGoals?.cyclePoints));
  const [message, setMessage] = useState<Message>(null);

  const save = useMutation({
    mutationFn: (rewardGoals: RewardGoals) => api.patch('/api/settings', { rewardGoals }),
    onSuccess: async () => {
      setMessage({ kind: 'status', text: t('settings.saved') });
      await Promise.all([queryClient.invalidateQueries({ queryKey: queryKeys.settings }), queryClient.invalidateQueries({ queryKey: ['points'] })]);
    },
    onError: () => setMessage({ kind: 'alert', text: t('app.error') }),
  });

  if (profile?.role !== 'admin') return null;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const weekPoints = parseRewardGoal(week);
    const cyclePoints = parseRewardGoal(cycle);
    if (weekPoints === undefined || cyclePoints === undefined) {
      setMessage({ kind: 'alert', text: t('settings.reward.invalid') });
      return;
    }
    setMessage(null);
    save.mutate({ weekPoints, cyclePoints });
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
            min={MIN_REWARD_GOAL_POINTS}
            max={MAX_REWARD_GOAL_POINTS}
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
            min={MIN_REWARD_GOAL_POINTS}
            max={MAX_REWARD_GOAL_POINTS}
            step="1"
            className="h-10 bg-card sm:max-w-48"
            placeholder={t('settings.reward.automatic')}
            value={cycle}
            onChange={(event) => setCycle(event.target.value)}
          />
        </Field>
      </div>
      <p className="text-sm text-muted-foreground">{t('settings.reward.hint')}</p>
      {message && <FormMessage kind={message.kind}>{message.text}</FormMessage>}
      <FormActions>
        <Button type="submit" disabled={save.isPending}>
          <Save aria-hidden="true" />
          {t('settings.reward.save')}
        </Button>
      </FormActions>
    </form>
  );
}

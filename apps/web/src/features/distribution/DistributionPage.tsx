import { Scale } from 'lucide-react';
import { useSearchParams } from 'react-router';
import { EmptyState } from '@/components/EmptyState';
import { NativeSelect } from '@/components/NativeSelect';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/button';
import { Label } from '@/components/ui/label';
import { panelTabsListClass, panelTabsTriggerClass, Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { useRooms, useTasks } from '../../api/queries.ts';
import { useSettings } from '../../api/v2/household.ts';
import { t } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { usePersistedFilter } from '../../hooks/usePersistedFilter.ts';
import { AllocationOverview } from '../planner/AllocationOverview.tsx';
import { usePlansV1 as usePlans } from '../planner/plansV1.ts';

export function DistributionPage() {
  const plans = usePlans();
  const tasks = useTasks();
  const rooms = useRooms();
  const settings = useSettings();
  const { activeUsers, profile } = useProfile();
  const [selectedPlanId, setSelectedPlanId, resetPlan] = usePersistedFilter<string | null>('distribution.plan', profile?.id ?? null, null);
  const [searchParams, setSearchParams] = useSearchParams();
  const selectedTab = searchParams.get('tab') === 'spacing' ? 'spacing' : 'workload';

  if (plans.isPending || tasks.isPending || rooms.isPending || settings.isPending) {
    return (
      <p role="status" className="text-muted-foreground">
        {t('app.loading')}
      </p>
    );
  }
  if (plans.isError || tasks.isError || rooms.isError || settings.isError) {
    return (
      <p role="alert" className="rounded-xl bg-destructive/10 p-4 text-destructive">
        {t('app.error')}
      </p>
    );
  }

  const visiblePlans = plans.data.filter((plan) => !plan.discarded);
  const plan =
    visiblePlans.find((item) => item._id === selectedPlanId) ??
    visiblePlans.find((item) => item.active) ??
    visiblePlans[0];
  const actions = plan ? (
    <div className="flex items-center gap-2">
      <Label htmlFor="distribution-plan">{t('planner.plan')}</Label>
      <NativeSelect
        id="distribution-plan"
        value={plan._id}
        onChange={(event) => setSelectedPlanId(event.target.value)}
      >
        {visiblePlans.map((item) => (
          <option key={item._id} value={item._id}>
            {item.name} {item.active ? t('planner.activeSuffix') : ''}
          </option>
        ))}
      </NativeSelect>
      <Button type="button" variant="ghost" onClick={resetPlan}>{t('distribution.resetFilter')}</Button>
    </div>
  ) : undefined;

  return (
    <section className="flex flex-col gap-5">
      <PageHeader
        title={t('nav.distribution')}
        description={t('distribution.explainer')}
        actions={actions}
      />
      {!plan ? (
        <EmptyState icon={<Scale className="size-6" aria-hidden="true" />}>
          {t('planner.noPlan')}
        </EmptyState>
      ) : (
        <Tabs
          value={selectedTab}
          onValueChange={(tab) => {
            const next = new URLSearchParams(searchParams);
            next.set('tab', tab);
            setSearchParams(next);
          }}
          className="gap-4"
        >
          <TabsList className={panelTabsListClass} aria-label={t('distribution.tabs')}>
            <TabsTrigger className={panelTabsTriggerClass} value="workload">{t('planner.distribution')}</TabsTrigger>
            <TabsTrigger className={panelTabsTriggerClass} value="spacing">{t('planner.spacing')}</TabsTrigger>
          </TabsList>
          <TabsContent value="workload" className="grid gap-4">
            <AllocationOverview
              slots={plan.slots}
              tasks={tasks.data.filter((task) => task.active)}
              users={activeUsers}
              rooms={rooms.data}
              intervals={settings.data.intervals}
              view="workload"
            />
          </TabsContent>
          <TabsContent value="spacing">
            <AllocationOverview
              slots={plan.slots}
              tasks={tasks.data.filter((task) => task.active)}
              users={activeUsers}
              rooms={rooms.data}
              intervals={settings.data.intervals}
              view="spacing"
            />
          </TabsContent>
        </Tabs>
      )}
    </section>
  );
}

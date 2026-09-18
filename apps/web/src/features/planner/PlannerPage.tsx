import { useState } from 'react';
import {
  CalendarRange,
  Copy,
  FileDown,
  Pencil,
  Power,
  RotateCcw,
  Save,
  Trash2,
  X,
} from 'lucide-react';
import { EmptyState } from '@/components/EmptyState';
import { NativeSelect } from '@/components/NativeSelect';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from '@/components/ui/sheet';
import { useRooms, useSettings, useTasks } from '../../api/queries.ts';
import { format, t } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { ExportDialog } from '../export/ExportDialog.tsx';
import { AiPage } from '../ai/AiPage.tsx';
import { PromoteBanner } from '../promote/PromoteBanner.tsx';
import {
  useActivatePlan,
  useCreatePlan,
  useDeletePlan,
  usePlans,
  usePutSlots,
  useUpdatePlan,
} from './api.ts';
import { PlanEditor } from './PlanEditor.tsx';

export function PlannerPage() {
  const plans = usePlans();
  const tasks = useTasks();
  const rooms = useRooms();
  const settings = useSettings();
  const { activeUsers } = useProfile();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [confirmActivate, setConfirmActivate] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [exportOpen, setExportOpen] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [confirmReset, setConfirmReset] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [plansOpen, setPlansOpen] = useState(false);
  const [planName, setPlanName] = useState('');
  const createPlan = useCreatePlan();
  const activatePlan = useActivatePlan();
  const updatePlan = useUpdatePlan();
  const resetPlan = usePutSlots();
  const deletePlan = useDeletePlan();

  if (plans.isPending || tasks.isPending || rooms.isPending || settings.isPending)
    return (
      <p role="status" className="text-muted-foreground">
        {t('app.loading')}
      </p>
    );
  if (plans.isError || tasks.isError || rooms.isError || settings.isError)
    return (
      <p role="alert" className="rounded-xl bg-destructive/10 p-4 text-destructive">
        {t('app.error')}
      </p>
    );

  const visiblePlans = plans.data.filter((p) => !p.discarded);
  const plan =
    visiblePlans.find((p) => p._id === selectedId) ??
    visiblePlans.find((p) => p.active) ??
    visiblePlans[0];
  const defaultPlan = visiblePlans[0];
  const isDefaultPlan = plan?._id === defaultPlan?._id;

  return (
    <section className="flex flex-col gap-5">
      {plan && (
            <Sheet open={plansOpen} onOpenChange={setPlansOpen}>
              <SheetContent className="overflow-y-auto sm:max-w-lg">
                <SheetHeader className="border-b pr-12">
                  <SheetTitle>{t('planner.manage')}</SheetTitle>
                  <SheetDescription>{t('planner.manageDescription')}</SheetDescription>
                </SheetHeader>
                <div className="grid gap-3 px-4 pb-6 [&>button]:w-full [&>button]:justify-start">
              <div className="flex items-center gap-2">
                <Label htmlFor="planner-plan-select" className="text-muted-foreground">
                  {t('planner.plan')}
                </Label>
                <NativeSelect
                  id="planner-plan-select"
                  className="w-56"
                  value={plan._id}
                  onChange={(e) => {
                    setSelectedId(e.target.value);
                    setRenaming(false);
                    setConfirmReset(false);
                    setConfirmDelete(false);
                  }}
                >
                  {visiblePlans.map((p) => (
                    <option key={p._id} value={p._id}>
                      {p.name} {p.active ? t('planner.activeSuffix') : ''}
                    </option>
                  ))}
                </NativeSelect>
              </div>
              {renaming ? (
                <form
                  className="flex items-center gap-2"
                  onSubmit={(event) => {
                    event.preventDefault();
                    const name = planName.trim();
                    if (!name) return;
                    updatePlan.mutate(
                      { planId: plan._id, patch: { name } },
                      { onSuccess: () => setRenaming(false) },
                    );
                  }}
                >
                  <Label htmlFor="planner-plan-name" className="visually-hidden">
                    {t('planner.name')}
                  </Label>
                  <Input
                    id="planner-plan-name"
                    className="h-10 w-52 bg-card"
                    value={planName}
                    autoFocus
                    onChange={(event) => setPlanName(event.target.value)}
                  />
                  <Button
                    type="submit"
                    size="icon"
                    aria-label={t('planner.renameSave')}
                    disabled={!planName.trim() || updatePlan.isPending}
                  >
                    <Save aria-hidden="true" />
                  </Button>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    aria-label={t('common.cancel')}
                    onClick={() => setRenaming(false)}
                  >
                    <X aria-hidden="true" />
                  </Button>
                </form>
              ) : (
                <Button
                  type="button"
                  variant="outline"
                  onClick={() => {
                    setPlanName(plan.name);
                    setRenaming(true);
                    updatePlan.reset();
                  }}
                >
                  <Pencil aria-hidden="true" />
                  {t('planner.rename')}
                </Button>
              )}
              <Button
                type="button"
                variant="outline"
                disabled={createPlan.isPending}
                onClick={() =>
                  createPlan.mutate(
                    { name: format('planner.copyName', { name: plan.name }), copyFromId: plan._id },
                    {
                      onSuccess: (created) => {
                        setSelectedId(created._id);
                        setPlansOpen(false);
                      },
                    },
                  )
                }
              >
                <Copy aria-hidden="true" />
                {t('planner.copy')}
              </Button>
              {!plan.active && (
                <Button
                  type="button"
                  onClick={() => {
                    setPlansOpen(false);
                    setConfirmActivate(true);
                  }}
                >
                  <Power aria-hidden="true" />
                  {t('planner.activate')}
                </Button>
              )}
              <Button
                type="button"
                variant="outline"
                disabled={plan.slots.length === 0 || resetPlan.isPending}
                onClick={() => {
                  setPlansOpen(false);
                  setConfirmReset(true);
                }}
              >
                <RotateCcw aria-hidden="true" />
                {t('planner.reset')}
              </Button>
              <Button
                type="button"
                variant="outline"
                className="text-destructive hover:text-destructive"
                disabled={isDefaultPlan || plan.active || deletePlan.isPending}
                title={
                  isDefaultPlan
                    ? t('planner.deleteDefaultDisabled')
                    : plan.active
                      ? t('planner.deleteActiveDisabled')
                      : undefined
                }
                onClick={() => {
                  setPlansOpen(false);
                  setConfirmDelete(true);
                }}
              >
                <Trash2 aria-hidden="true" />
                {t('planner.delete')}
              </Button>
              <Button
                type="button"
                variant="outline"
                onClick={() => {
                  setPlansOpen(false);
                  setExportOpen(true);
                }}
              >
                <FileDown aria-hidden="true" />
                {t('export.open')}
              </Button>
              <section
                aria-label={t('settings.ai.title')}
                className="mt-3 border-t pt-6"
              >
                <AiPage section="plan" embedded />
              </section>
                </div>
              </SheetContent>
            </Sheet>
      )}

      <PromoteBanner />

      <Dialog open={confirmReset} onOpenChange={setConfirmReset}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('planner.resetConfirmTitle')}</DialogTitle>
            <DialogDescription>{t('planner.resetConfirmBody')}</DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button type="button" variant="ghost" onClick={() => setConfirmReset(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              type="button"
              disabled={!plan || resetPlan.isPending}
              onClick={() => {
                if (!plan) return;
                resetPlan.mutate(
                  { planId: plan._id, slots: [] },
                  {
                    onSuccess: () => {
                      setConfirmReset(false);
                      setNotice(t('planner.resetDone'));
                    },
                  },
                );
              }}
            >
              <RotateCcw aria-hidden="true" />
              {t('planner.resetConfirm')}
            </Button>
          </DialogFooter>
          {resetPlan.isError && (
            <p role="alert" className="text-sm font-semibold text-destructive">
              {t('planner.resetError')}
            </p>
          )}
        </DialogContent>
      </Dialog>

      <Dialog open={confirmDelete} onOpenChange={setConfirmDelete}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('planner.deleteConfirmTitle')}</DialogTitle>
            <DialogDescription>
              {format('planner.deleteConfirmBody', { name: plan?.name ?? '' })}
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button type="button" variant="ghost" onClick={() => setConfirmDelete(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              type="button"
              variant="destructive"
              disabled={!plan || isDefaultPlan || plan.active || deletePlan.isPending}
              onClick={() => {
                if (!plan || !defaultPlan) return;
                deletePlan.mutate(plan._id, {
                  onSuccess: () => {
                    setSelectedId(defaultPlan._id);
                    setConfirmDelete(false);
                    setNotice(t('planner.deleteDone'));
                  },
                });
              }}
            >
              <Trash2 aria-hidden="true" />
              {t('planner.deleteConfirm')}
            </Button>
          </DialogFooter>
          {deletePlan.isError && (
            <p role="alert" className="text-sm font-semibold text-destructive">
              {t('planner.deleteError')}
            </p>
          )}
        </DialogContent>
      </Dialog>

      {updatePlan.isError && (
        <p role="alert" className="rounded-xl bg-destructive/10 p-3 text-sm font-semibold text-destructive">
          {t('planner.renameError')}
        </p>
      )}

      <div className="flex min-w-0 flex-col gap-5">
          {!plan ? (
            <EmptyState icon={<CalendarRange className="size-6" aria-hidden="true" />}>
              <p>{t('planner.noPlan')}</p>
            </EmptyState>
          ) : (
            <>
              {notice && (
                <p
                  role="status"
                  className="rounded-xl border border-success/30 bg-success/10 px-4 py-2 text-sm font-semibold text-success"
                >
                  {notice}
                </p>
              )}
              {exportOpen && <ExportDialog onClose={() => setExportOpen(false)} />}

              {confirmActivate && (
            <div
              role="dialog"
              aria-modal="true"
              aria-labelledby="activate-title"
              className="max-w-xl rounded-2xl border-2 border-primary/30 bg-card p-6 shadow-md"
            >
              <h2 id="activate-title" className="flex items-center gap-2">
                <Power className="size-5 text-primary" aria-hidden="true" />
                {t('planner.activate.confirmTitle')}
              </h2>
              <p className="mt-2 text-sm text-muted-foreground">
                {t('planner.activate.confirmBody')}
              </p>
              <div className="mt-5 flex flex-wrap gap-2">
                <Button
                  type="button"
                  disabled={activatePlan.isPending}
                  onClick={() =>
                    activatePlan.mutate(plan._id, {
                      onSuccess: () => {
                        setConfirmActivate(false);
                        setNotice(t('planner.activated'));
                      },
                    })
                  }
                >
                  {t('planner.activate.confirm')}
                </Button>
                <Button type="button" variant="ghost" onClick={() => setConfirmActivate(false)}>
                  {t('common.cancel')}
                </Button>
              </div>
              {activatePlan.isError && (
                <p role="alert" className="mt-3 text-sm text-destructive">
                  {t('app.error')}
                </p>
              )}
            </div>
              )}

              <PlanEditor
                key={plan._id}
                plan={plan}
                tasks={tasks.data.filter((task) => task.active)}
                rooms={rooms.data}
                users={activeUsers}
                intervals={settings.data.intervals}
                onManagePlans={() => setPlansOpen(true)}
              />
            </>
          )}
      </div>
    </section>
  );
}

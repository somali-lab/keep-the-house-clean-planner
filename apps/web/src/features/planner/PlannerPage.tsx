import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
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
import { useSettings } from '../../api/queries.ts';
import { useRooms, useTasks } from '../../api/v2/queries.ts';
import { format, t } from '../../i18n/nl.ts';
import { ApiRequestError, isStaleEntity } from '../../api/index.ts';
import { getLocale } from '../../i18n/runtime.ts';
import { useProfile } from '../../identity/index.ts';
import { ExportDialog } from '../export/ExportDialog.tsx';
import { AiDraftCard } from '../ai/AiDraftCard.tsx';
import { AiPage } from '../ai/AiPage.tsx';
import type { AiProposal } from '../ai/api.ts';
import { PromoteBanner } from '../promote/PromoteBanner.tsx';
import {
  planKeys,
  useActivatePlan,
  useActivationPreview,
  useCreatePlan,
  useDeletePlan,
  usePlans,
  usePutSlots,
  useUpdatePlan,
  type ActivationPreviewItem,
  type CyclePlan,
} from './api.ts';
import { PlanEditor } from './PlanEditor.tsx';

export function PlannerPage() {
  const queryClient = useQueryClient();
  const plans = usePlans();
  const tasks = useTasks();
  const rooms = useRooms();
  const settings = useSettings();
  const { activeUsers, profile } = useProfile();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [confirmActivate, setConfirmActivate] = useState(false);
  const [stalePreview, setStalePreview] = useState(false);
  const [reviewedFreshPreview, setReviewedFreshPreview] = useState(false);
  const activationPreview = useActivationPreview(selectedId, confirmActivate);
  const [notice, setNotice] = useState<string | null>(null);
  const [exportOpen, setExportOpen] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [confirmReset, setConfirmReset] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [plansOpen, setPlansOpen] = useState(false);
  const [planName, setPlanName] = useState('');
  // Warnings are only shown right after creation, for the draft that was just created.
  const [createdProposal, setCreatedProposal] = useState<AiProposal | null>(null);
  // Bumped after a reset, so that the editor starts again from the emptied plan.
  const [editorEpoch, setEditorEpoch] = useState(0);
  // Raised when a reset starts, so that a drop still waiting to be saved does not land on the emptied plan.
  const [discardToken, setDiscardToken] = useState(0);
  const aiDraftCardRef = useRef<HTMLElement | null>(null);
  const focusAiDraftRef = useRef(false);
  const createPlan = useCreatePlan();
  const activatePlan = useActivatePlan();
  const updatePlan = useUpdatePlan();
  const resetPlan = usePutSlots();
  const deletePlan = useDeletePlan();

  // The dialogs start clean: an error of an earlier attempt does not greet the next one.
  const openReset = () => {
    resetPlan.reset();
    setConfirmReset(true);
  };
  const closeReset = () => {
    resetPlan.reset();
    setConfirmReset(false);
  };
  const openDelete = () => {
    deletePlan.reset();
    setConfirmDelete(true);
  };
  const closeDelete = () => {
    deletePlan.reset();
    setConfirmDelete(false);
  };

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
    visiblePlans.find((p) => p.id === selectedId) ??
    visiblePlans.find((p) => p.active) ??
    visiblePlans[0];
  const defaultPlan = visiblePlans[0];
  const isDefaultPlan = plan?.id === defaultPlan?.id;
  // An AI draft that was activated or discarded is no longer a concept.
  const isAiDraft = plan !== undefined && plan.draft && !plan.active && plan.source === 'ai';

  return (
    <section className="flex flex-col gap-5">
      {plan && (
        <Sheet open={plansOpen} onOpenChange={setPlansOpen}>
          <SheetContent
            className="overflow-y-auto sm:max-w-lg"
            onCloseAutoFocus={(event) => {
              if (!focusAiDraftRef.current) return;
              focusAiDraftRef.current = false;
              event.preventDefault();
              aiDraftCardRef.current?.focus();
            }}
          >
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
                  value={plan.id}
                  onChange={(e) => {
                    setSelectedId(e.target.value);
                    setCreatedProposal(null);
                    setRenaming(false);
                    updatePlan.reset();
                    closeReset();
                    closeDelete();
                  }}
                >
                  {visiblePlans.map((p) => (
                    <option key={p.id} value={p.id}>
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
                      { planId: plan.id, version: plan.version, patch: { name } },
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
                    onClick={() => {
                      updatePlan.reset();
                      setRenaming(false);
                    }}
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
              {updatePlan.isError && (
                <p role="alert" className="rounded-xl bg-destructive/10 p-3 text-sm font-semibold text-destructive">
                  {isStaleEntity(updatePlan.error) ? t('app.staleEntity') : t('planner.renameError')}
                </p>
              )}
              <Button
                type="button"
                variant="outline"
                disabled={createPlan.isPending}
                onClick={() =>
                  createPlan.mutate(
                    { name: format('planner.copyName', { name: plan.name }), copyFromId: plan.id },
                    {
                      onSuccess: (created) => {
                        setSelectedId(created.id);
                        setCreatedProposal(null);
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
                    activatePlan.reset();
                    setStalePreview(false);
                    setReviewedFreshPreview(false);
                    setNotice(null);
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
                  openReset();
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
                  openDelete();
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
              <section aria-label={t('settings.ai.title')} className="mt-3 border-t pt-6">
                <AiPage
                  section="plan"
                  embedded
                  onPlanCreated={(result) => {
                    setSelectedId(result.planId);
                    setCreatedProposal(result);
                    setRenaming(false);
                    closeReset();
                    closeDelete();
                    setConfirmActivate(false);
                    setNotice(null);
                    focusAiDraftRef.current = true;
                    setPlansOpen(false);
                  }}
                />
              </section>
            </div>
          </SheetContent>
        </Sheet>
      )}

      <PromoteBanner />

      <Dialog open={confirmReset} onOpenChange={(open) => (open ? openReset() : closeReset())}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('planner.resetConfirmTitle')}</DialogTitle>
            <DialogDescription>{t('planner.resetConfirmBody')}</DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button type="button" variant="ghost" onClick={closeReset}>
              {t('common.cancel')}
            </Button>
            <Button
              type="button"
              disabled={!plan || resetPlan.isPending}
              onClick={() => {
                if (!plan) return;
                setDiscardToken((token) => token + 1);
                resetPlan.mutate(
                  { planId: plan.id, version: plan.version, slots: [] },
                  {
                    onSuccess: () => {
                      closeReset();
                      setEditorEpoch((epoch) => epoch + 1);
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
              {isStaleEntity(resetPlan.error) ? t('app.staleEntity') : t('planner.resetError')}
            </p>
          )}
        </DialogContent>
      </Dialog>

      <Dialog open={confirmDelete} onOpenChange={(open) => (open ? openDelete() : closeDelete())}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('planner.deleteConfirmTitle')}</DialogTitle>
            <DialogDescription>
              {format('planner.deleteConfirmBody', { name: plan?.name ?? '' })}
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button type="button" variant="ghost" onClick={closeDelete}>
              {t('common.cancel')}
            </Button>
            <Button
              type="button"
              variant="destructive"
              disabled={!plan || isDefaultPlan || plan.active || deletePlan.isPending}
              onClick={() => {
                if (!plan || !defaultPlan) return;
                deletePlan.mutate(
                  { planId: plan.id, version: plan.version },
                  {
                    onSuccess: () => {
                      setSelectedId(defaultPlan.id);
                      closeDelete();
                      setNotice(t('planner.deleteDone'));
                    },
                    onError: (error) => {
                      if (!isStaleEntity(error)) return;
                      // The plans are read again by now: a plan that is gone needs no delete, and the dialog must not offer another one.
                      const left = queryClient.getQueryData<CyclePlan[]>(planKeys.all) ?? [];
                      if (!left.some((candidate) => candidate.id === plan.id)) {
                        setSelectedId(defaultPlan.id);
                        closeDelete();
                      }
                    },
                  },
                );
              }}
            >
              <Trash2 aria-hidden="true" />
              {t('planner.deleteConfirm')}
            </Button>
          </DialogFooter>
          {deletePlan.isError && (
            <p role="alert" className="text-sm font-semibold text-destructive">
              {isStaleEntity(deletePlan.error) ? t('app.staleEntity') : t('planner.deleteError')}
            </p>
          )}
        </DialogContent>
      </Dialog>

      <div className="flex min-w-0 flex-col gap-5">
        {!plan ? (
          <EmptyState icon={<CalendarRange className="size-6" aria-hidden="true" />}>
            <p>{t('planner.noPlan')}</p>
          </EmptyState>
        ) : (
          <>
            {isAiDraft && createdProposal?.planId === plan.id && (
              <p
                role="status"
                className="rounded-xl border border-success/30 bg-success/10 px-4 py-2 text-sm font-semibold text-success"
              >
                {t('planner.aiDraftOpened')}
              </p>
            )}
            {notice && (
              <p
                role="status"
                className="rounded-xl border border-success/30 bg-success/10 px-4 py-2 text-sm font-semibold text-success"
              >
                {notice}
              </p>
            )}
            {exportOpen && <ExportDialog onClose={() => setExportOpen(false)} />}

            {isAiDraft && (
              <AiDraftCard
                cardRef={aiDraftCardRef}
                onManage={() => setPlansOpen(true)}
                rationale={plan.rationale}
                warnings={createdProposal?.planId === plan.id ? createdProposal.warnings : []}
              />
            )}

            {!plan.active && !isAiDraft && (
              <p
                role="note"
                className="rounded-xl border border-border bg-muted/50 px-4 py-3 text-sm"
              >
                {t('planner.inactivePlanNotice')}
              </p>
            )}

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
                {activationPreview.isPending || activationPreview.isFetching ? (
                  <p role="status" className="mt-4 text-sm text-muted-foreground">
                    {t('planner.activate.previewLoading')}
                  </p>
                ) : activationPreview.isError ? (
                  <p role="alert" className="mt-4 text-sm text-destructive">
                    {t('planner.activate.previewError')}
                  </p>
                ) : activationPreview.data ? (
                  <div className="mt-4 max-h-80 space-y-4 overflow-y-auto rounded-lg border p-4">
                    <p className="text-sm text-muted-foreground">
                      {format('planner.activate.previewAsOf', {
                        date: formatPreviewDate(activationPreview.data.asOfDate),
                      })}
                    </p>
                    <ActivationPreviewSection
                      title={t('planner.activate.removed')}
                      items={activationPreview.data.removed}
                      activeUsers={activeUsers}
                    />
                    <ActivationPreviewSection
                      title={t('planner.activate.added')}
                      items={activationPreview.data.added}
                      activeUsers={activeUsers}
                    />
                    <section aria-label={t('planner.activate.preserved')}>
                      <h3 className="font-semibold">
                        {t('planner.activate.preserved')} (
                        {Object.values(activationPreview.data.preserved).reduce(
                          (sum, items) => sum + items.length,
                          0,
                        )}
                        )
                      </h3>
                      {(['done', 'skipped', 'moved', 'adhoc'] as const).map((kind) => (
                        <ActivationPreviewSection
                          key={kind}
                          title={t(`planner.activate.preserved.${kind}`)}
                          items={activationPreview.data!.preserved[kind]}
                          activeUsers={activeUsers}
                        />
                      ))}
                    </section>
                    <p className="text-sm text-muted-foreground">
                      {t('planner.activate.skippedExplanation')}
                    </p>
                  </div>
                ) : null}
                {stalePreview && (
                  <div
                    role="alert"
                    className="mt-4 rounded-lg border border-destructive/30 bg-destructive/5 p-3 text-sm"
                  >
                    <p>{t('planner.activate.stale')}</p>
                    <label className="mt-2 flex items-start gap-2">
                      <input
                        type="checkbox"
                        checked={reviewedFreshPreview}
                        onChange={(event) => setReviewedFreshPreview(event.target.checked)}
                      />
                      <span>{t('planner.activate.reviewed')}</span>
                    </label>
                  </div>
                )}
                <div className="mt-5 flex flex-wrap gap-2">
                  <Button
                    type="button"
                    disabled={
                      activatePlan.isPending ||
                      activationPreview.isPending ||
                      activationPreview.isFetching ||
                      activationPreview.isError ||
                      !activationPreview.data ||
                      (stalePreview && !reviewedFreshPreview)
                    }
                    onClick={() => {
                      const preview = activationPreview.data;
                      if (!preview) return;
                      setStalePreview(false);
                      setReviewedFreshPreview(false);
                      activatePlan.mutate(
                        { planId: plan.id, previewToken: preview.previewToken },
                        {
                          onSuccess: () => {
                            setConfirmActivate(false);
                            setNotice(t('planner.activated'));
                          },
                          onError: (error) => {
                            if (
                              error instanceof ApiRequestError &&
                              error.code === 'stale_activation_preview'
                            ) {
                              setReviewedFreshPreview(false);
                              setStalePreview(true);
                              void activationPreview.refetch();
                            }
                          },
                        },
                      );
                    }}
                  >
                    {t('planner.activate.confirm')}
                  </Button>
                  <Button type="button" variant="ghost" onClick={() => setConfirmActivate(false)}>
                    {t('common.cancel')}
                  </Button>
                </div>
                {activatePlan.isError &&
                  !(
                    activatePlan.error instanceof ApiRequestError &&
                    activatePlan.error.code === 'stale_activation_preview'
                  ) && (
                    <p role="alert" className="mt-3 text-sm text-destructive">
                      {t('app.error')}
                    </p>
                  )}
              </div>
            )}

            <PlanEditor
              key={`${plan.id}:${editorEpoch}`}
              plan={plan}
              tasks={tasks.data.filter((task) => task.active)}
              allTasks={tasks.data}
              discardPendingToken={discardToken}
              rooms={rooms.data}
              users={activeUsers}
              profileId={profile?._id ?? null}
              intervals={settings.data.intervals}
              onManagePlans={() => setPlansOpen(true)}
            />
          </>
        )}
      </div>
    </section>
  );
}

function ActivationPreviewSection({
  title,
  items,
  activeUsers,
}: {
  title: string;
  items: ActivationPreviewItem[];
  activeUsers: { _id: string; name: string }[];
}) {
  return (
    <section className="mt-3" aria-label={title}>
      <h3 className="font-semibold">
        {title} ({items.length})
      </h3>
      {items.length === 0 ? (
        <p className="mt-1 text-sm text-muted-foreground">{t('planner.activate.none')}</p>
      ) : (
        <ul className="mt-1 space-y-1 text-sm">
          {items.map((item, index) => {
            const person =
              activeUsers.find((user) => user._id === item.assigneeId)?.name ??
              (item.assigneeId ? t('planner.activate.unknownPerson') : t('planner.anyone'));
            const date = formatPreviewDate(item.date);
            return (
              <li
                key={`${item.occurrenceId ?? item.taskId ?? "none"}-${item.date}-${index}`}
                className="rounded-md bg-muted/50 px-2 py-1"
              >
                <span className="font-medium">{item.taskName}</span>
                <span className="text-muted-foreground">
                  {' '}
                  · {date} · {person}
                </span>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

function formatPreviewDate(dayKey: string): string {
  return new Intl.DateTimeFormat(getLocale(), { dateStyle: 'medium' }).format(
    new Date(`${dayKey}T12:00:00`),
  );
}

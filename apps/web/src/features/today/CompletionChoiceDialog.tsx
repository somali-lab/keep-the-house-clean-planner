import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog';
import { useUsers } from '../../api/v2/household.ts';
import { format, t } from '../../i18n/nl.ts';

/** Name and activity of the assignee, also when the profile is no longer active (the server refuses to credit them). */
export function useAssigneeChoice(assigneeId: string | null): { name: string; active: boolean } {
  const users = useUsers();
  const user = assigneeId ? users.data?.find((candidate) => candidate.id === assigneeId) : undefined;
  return { name: user?.name ?? t('tasks.unknownUser'), active: user?.active === true };
}

interface CompletionChoiceDialogProps {
  task: string;
  assignee: string;
  /** False when the assignee is no longer active: checking off for them is not offered. */
  assigneeActive?: boolean;
  open: boolean;
  onOpenChange(open: boolean): void;
  onCompleteForAssignee(): void;
  onTakeOver(): void;
}

/** Explicitly distinguishes helping someone check off their task from taking it over. */
export function CompletionChoiceDialog({
  task,
  assignee,
  assigneeActive = true,
  open,
  onOpenChange,
  onCompleteForAssignee,
  onTakeOver,
}: CompletionChoiceDialogProps) {
  return (
    <AlertDialog open={open} onOpenChange={onOpenChange}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{format('completionChoice.title', { task })}</AlertDialogTitle>
          <AlertDialogDescription>
            {format(assigneeActive ? 'completionChoice.description' : 'completionChoice.descriptionInactive', { assignee })}
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter className="sm:flex-col">
          <AlertDialogCancel>{t('common.cancel')}</AlertDialogCancel>
          {assigneeActive && (
            <AlertDialogAction variant="outline" onClick={onCompleteForAssignee}>
              {format('completionChoice.forAssignee', { assignee })}
            </AlertDialogAction>
          )}
          <AlertDialogAction onClick={onTakeOver}>
            {t('completionChoice.takeOver')}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

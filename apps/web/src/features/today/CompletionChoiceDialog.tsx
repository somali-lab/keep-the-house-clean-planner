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
import { format, t } from '../../i18n/nl.ts';

interface CompletionChoiceDialogProps {
  task: string;
  assignee: string;
  open: boolean;
  onOpenChange(open: boolean): void;
  onCompleteForAssignee(): void;
  onTakeOver(): void;
}

/** Explicitly distinguishes helping someone check off their task from taking it over. */
export function CompletionChoiceDialog({
  task,
  assignee,
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
            {format('completionChoice.description', { assignee })}
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter className="sm:flex-col">
          <AlertDialogCancel>{t('common.cancel')}</AlertDialogCancel>
          <AlertDialogAction variant="outline" onClick={onCompleteForAssignee}>
            {format('completionChoice.forAssignee', { assignee })}
          </AlertDialogAction>
          <AlertDialogAction onClick={onTakeOver}>
            {t('completionChoice.takeOver')}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

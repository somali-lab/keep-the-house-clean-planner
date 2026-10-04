import { useEffect, useRef, useState } from 'react';
import { Button } from '@/components/ui/button';
import { format, t } from '../i18n/nl.ts';

/**
 * The foot of a list that loads page by page: a polite status ("N getoond" while more is waiting, "alles geladen" at the end)
 * and the button for the next page. The button stays focusable while the page loads (`aria-disabled`, so a keyboard user keeps
 * their place), and when the last page arrives the focus moves to the status instead of falling to the top of the document.
 */
export function LoadMore({
  count,
  hasNextPage,
  isFetchingNextPage,
  onLoadMore,
}: {
  count: number;
  hasNextPage: boolean;
  isFetchingNextPage: boolean;
  onLoadMore: () => void;
}) {
  const statusRef = useRef<HTMLParagraphElement>(null);
  const [asked, setAsked] = useState(false);
  useEffect(() => {
    if (asked && !hasNextPage && !isFetchingNextPage) {
      statusRef.current?.focus();
      setAsked(false);
    }
  }, [asked, hasNextPage, isFetchingNextPage]);
  if (count === 0 && !hasNextPage) return null;
  return (
    <div className="mt-6 flex flex-col items-center gap-3">
      <p
        ref={statusRef}
        tabIndex={-1}
        aria-live="polite"
        className="text-sm text-muted-foreground outline-none"
      >
        {format(hasNextPage ? 'paging.shown' : 'paging.allLoaded', { count })}
      </p>
      {hasNextPage && (
        <Button
          type="button"
          variant="outline"
          aria-disabled={isFetchingNextPage}
          onClick={() => {
            if (isFetchingNextPage) return;
            setAsked(true);
            onLoadMore();
          }}
        >
          {isFetchingNextPage ? t('app.loading') : t('paging.loadMore')}
        </Button>
      )}
    </div>
  );
}

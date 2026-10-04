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
  error = false,
  onRetry,
}: {
  count: number;
  hasNextPage: boolean;
  isFetchingNextPage: boolean;
  onLoadMore: () => void;
  /** A page or a background refresh failed while earlier pages are still shown: say so here and offer a retry. */
  error?: boolean;
  onRetry?: () => void;
}) {
  const statusRef = useRef<HTMLParagraphElement>(null);
  const alertRef = useRef<HTMLDivElement>(null);
  const [asked, setAsked] = useState(false);
  const [retried, setRetried] = useState(false);
  useEffect(() => {
    if (isFetchingNextPage) return;
    // The button the person used is gone now (a failure replaced it, or the last page arrived): keep the focus on the news.
    if (error && asked) {
      alertRef.current?.focus();
      setAsked(false);
    } else if (!error && (retried || (asked && !hasNextPage))) {
      statusRef.current?.focus();
      setAsked(false);
      setRetried(false);
    }
  }, [asked, retried, error, hasNextPage, isFetchingNextPage]);
  if (count === 0 && !hasNextPage && !error) return null;
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
      {error && onRetry && (
        <div ref={alertRef} tabIndex={-1} role="alert" className="outline-none flex flex-wrap items-center justify-center gap-3 rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          <span>{t('paging.error')}</span>
          <Button type="button" variant="outline" size="sm" aria-disabled={isFetchingNextPage} onClick={() => { if (!isFetchingNextPage) { setRetried(true); onRetry(); } }}>
            {t('paging.retry')}
          </Button>
        </div>
      )}
      {hasNextPage && !error && (
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

/** What a retry means for a paged query: the failed next page again (same cursor), or the failed refresh of the loaded pages. */
export function retryPaged(query: { isFetchNextPageError: boolean; fetchNextPage: () => unknown; refetch: () => unknown }): () => void {
  return () => void (query.isFetchNextPageError ? query.fetchNextPage() : query.refetch());
}

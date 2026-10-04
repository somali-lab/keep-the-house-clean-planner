/** A page of a bounded v2 list. */
export interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

const MAX_PAGES = 200;

/** Every item of a bounded list: the pages are followed until the last one. */
export async function collectPages<T>(fetchPage: (cursor: string | undefined) => Promise<Page<T>>): Promise<T[]> {
  const items: T[] = [];
  const seen = new Set<string>();
  let cursor: string | undefined;
  do {
    // A server that repeats a cursor, or never ends, must not keep the page loading forever.
    if (seen.size >= MAX_PAGES || (cursor && seen.has(cursor))) throw new Error('Paging did not end.');
    if (cursor) seen.add(cursor);
    const page = await fetchPage(cursor);
    items.push(...page.items);
    cursor = page.nextCursor ?? undefined;
  } while (cursor);
  return items;
}

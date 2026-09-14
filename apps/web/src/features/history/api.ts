import type { AuditEntity, AuditEntry } from '@huishoudplanner/shared';
import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/index.ts';

export interface AuditFilters {
  entity?: AuditEntity | '';
  entityId?: string;
  actorId?: string;
  /** ISO instants, inclusive. */
  from?: string;
  to?: string;
}

interface AuditPage {
  items: AuditEntry[];
  nextCursor: string | null;
}

export const AUDIT_PAGE_SIZE = 50;

export function auditUrl(filters: AuditFilters, cursor: string | null): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(filters)) {
    if (value) params.set(key, value);
  }
  params.set('limit', String(AUDIT_PAGE_SIZE));
  if (cursor) params.set('cursor', cursor);
  return `/api/audit?${params.toString()}`;
}

export function useAuditFeed(filters: AuditFilters) {
  return useInfiniteQuery({
    queryKey: ['audit', filters],
    initialPageParam: null as string | null,
    queryFn: async ({ pageParam }) => (await api.get<AuditPage>(auditUrl(filters, pageParam))).data,
    getNextPageParam: (last) => last.nextCursor,
  });
}

export function useClearAudit() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async () => (await api.delete<{ deleted: number }>('/api/audit')).data,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['audit'] }),
  });
}

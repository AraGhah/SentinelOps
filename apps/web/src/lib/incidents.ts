import 'server-only';
import { apiClient, ApiError } from '@/lib/api-client';
import { getValidSession } from '@/lib/auth/session';
import type { Incident, PagedResult } from '@/lib/types';

export const DEFAULT_PAGE_SIZE = 20;

export type IncidentsResult =
  | { status: 'unauthenticated' }
  | { status: 'no-organization' }
  | { status: 'error'; message: string }
  | { status: 'ready'; data: PagedResult<Incident> };

// Server-side fetch of the org-scoped incidents list. Must run in a Server
// Component or Server Action — it needs the httpOnly session cookie (for
// the bearer token) and the active organization id (see FE-02), neither of
// which a client component can read directly.
export async function getIncidents(
  page: number,
  pageSize: number = DEFAULT_PAGE_SIZE,
): Promise<IncidentsResult> {
  const session = await getValidSession();
  if (!session) return { status: 'unauthenticated' };
  if (!session.organizationId) return { status: 'no-organization' };

  try {
    const data = await apiClient.get<PagedResult<Incident>>(
      `/api/v1/organizations/${session.organizationId}/incidents?page=${page}&pageSize=${pageSize}`,
      { token: session.accessToken },
    );
    return { status: 'ready', data };
  } catch (error) {
    const message = error instanceof ApiError ? (error.detail ?? error.message) : 'Unexpected error';
    return { status: 'error', message };
  }
}

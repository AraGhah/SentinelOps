import type { Metadata } from 'next';
import { redirect } from 'next/navigation';
import { ShieldAlert } from 'lucide-react';
import { IncidentsView } from '@/components/incidents/incidents-view';
import { EmptyState } from '@/components/states/empty-state';
import { ErrorState } from '@/components/states/error-state';
import { getIncidents } from '@/lib/incidents';

export const metadata: Metadata = {
  title: 'Incidents — SentinelOps',
};

export default async function IncidentsPage({
  searchParams,
}: {
  searchParams: Promise<{ page?: string }>;
}) {
  const { page: pageParam } = await searchParams;
  const requestedPage = Number(pageParam);
  const page = Number.isFinite(requestedPage) && requestedPage > 0 ? Math.floor(requestedPage) : 1;

  const result = await getIncidents(page);

  if (result.status === 'unauthenticated') {
    redirect('/login');
  }

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Incidents</h1>
        <p className="text-sm text-muted-foreground">
          Security findings correlated into actionable incidents across your linked AWS accounts.
        </p>
      </div>

      {result.status === 'no-organization' && (
        <EmptyState
          icon={ShieldAlert}
          title="No organization"
          description="Your account isn't a member of any organization yet, so there are no incidents to show."
        />
      )}

      {result.status === 'error' && <ErrorState description={result.message} />}

      {result.status === 'ready' && <IncidentsView data={result.data} />}
    </div>
  );
}

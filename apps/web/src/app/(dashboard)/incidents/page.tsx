import type { Metadata } from 'next';
import { IncidentsView } from '@/components/incidents/incidents-view';

export const metadata: Metadata = {
  title: 'Incidents — SentinelOps',
};

export default function IncidentsPage() {
  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Incidents</h1>
        <p className="text-sm text-muted-foreground">
          Security findings correlated into actionable incidents across your linked AWS accounts.
        </p>
      </div>
      <IncidentsView />
    </div>
  );
}

import type { Metadata } from 'next';
import { Activity } from 'lucide-react';
import { EmptyState } from '@/components/states/empty-state';

export const metadata: Metadata = {
  title: 'Events — SentinelOps',
};

export default function EventsPage() {
  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Security Events</h1>
        <p className="text-sm text-muted-foreground">
          Raw normalized findings ingested from GuardDuty and Security Hub.
        </p>
      </div>
      <EmptyState
        icon={Activity}
        title="No events yet"
        description="Findings from your linked AWS accounts will appear here as they're ingested."
      />
    </div>
  );
}

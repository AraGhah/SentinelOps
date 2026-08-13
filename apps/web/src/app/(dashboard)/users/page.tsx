import type { Metadata } from 'next';
import { Users } from 'lucide-react';
import { EmptyState } from '@/components/states/empty-state';

export const metadata: Metadata = {
  title: 'Users — SentinelOps',
};

export default function UsersPage() {
  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Users</h1>
        <p className="text-sm text-muted-foreground">Manage who has access to your tenant.</p>
      </div>
      <EmptyState
        icon={Users}
        title="No users yet"
        description="Invite teammates to triage and respond to incidents together."
      />
    </div>
  );
}

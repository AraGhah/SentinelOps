import type { Metadata } from 'next';
import { SecuritySettings } from '@/components/settings/security-settings';

export const metadata: Metadata = {
  title: 'Settings — SentinelOps',
};

export default function SettingsPage() {
  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Settings</h1>
        <p className="text-sm text-muted-foreground">Tenant and notification preferences.</p>
      </div>
      <SecuritySettings />
    </div>
  );
}

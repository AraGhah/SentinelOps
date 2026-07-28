import type { Metadata } from "next";
import { Settings } from "lucide-react";
import { EmptyState } from "@/components/states/empty-state";

export const metadata: Metadata = {
  title: "Settings — SentinelOps",
};

export default function SettingsPage() {
  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Settings</h1>
        <p className="text-sm text-muted-foreground">Tenant and notification preferences.</p>
      </div>
      <EmptyState
        icon={Settings}
        title="Nothing to configure yet"
        description="Tenant settings will appear here in a future release."
      />
    </div>
  );
}

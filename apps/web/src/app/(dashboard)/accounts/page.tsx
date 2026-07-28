import type { Metadata } from "next";
import { Cloud } from "lucide-react";
import { EmptyState } from "@/components/states/empty-state";

export const metadata: Metadata = {
  title: "AWS Accounts — SentinelOps",
};

export default function AccountsPage() {
  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">AWS Accounts</h1>
        <p className="text-sm text-muted-foreground">
          Manage the AWS accounts linked to your tenant for security event ingestion.
        </p>
      </div>
      <EmptyState
        icon={Cloud}
        title="No AWS accounts linked"
        description="Link an AWS account to start ingesting GuardDuty and Security Hub findings."
      />
    </div>
  );
}

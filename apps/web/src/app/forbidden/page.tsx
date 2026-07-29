import type { Metadata } from "next";
import Link from "next/link";
import { ShieldAlert } from "lucide-react";
import { buttonVariants } from "@/components/ui/button";
import { cn } from "@/lib/utils";

export const metadata: Metadata = {
  title: "Access denied — SentinelOps",
};

export default function ForbiddenPage() {
  return (
    <div className="flex min-h-screen flex-col items-center justify-center gap-3 bg-muted/40 p-4 text-center">
      <ShieldAlert className="size-10 text-muted-foreground" />
      <h1 className="text-xl font-semibold tracking-tight">Access denied</h1>
      <p className="max-w-sm text-sm text-muted-foreground">
        Your account doesn&apos;t have permission to view this page. Contact your administrator if
        you believe this is a mistake.
      </p>
      <Link href="/dashboard" className={cn(buttonVariants({ variant: "default" }), "mt-2")}>
        Back to dashboard
      </Link>
    </div>
  );
}

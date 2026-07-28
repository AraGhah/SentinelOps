import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import type { Severity } from "@/lib/types";

const SEVERITY_STYLES: Record<Severity, string> = {
  Low: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300",
  Medium: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  High: "bg-orange-100 text-orange-800 dark:bg-orange-900/40 dark:text-orange-300",
  Critical: "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300",
};

export function SeverityBadge({ severity }: { severity: Severity }) {
  return <Badge className={cn("border-transparent", SEVERITY_STYLES[severity])}>{severity}</Badge>;
}

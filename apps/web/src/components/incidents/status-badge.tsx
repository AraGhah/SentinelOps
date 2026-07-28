import { Badge } from "@/components/ui/badge";
import type { IncidentStatus } from "@/lib/types";

export function StatusBadge({ status }: { status: IncidentStatus }) {
  const variant = status === "Closed" || status === "FalsePositive" ? "secondary" : "outline";
  return <Badge variant={variant}>{status}</Badge>;
}

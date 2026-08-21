import { Badge } from '@/components/ui/badge';
import { cn } from '@/lib/utils';
import { SEVERITY_LABELS, type IncidentSeverity } from '@/lib/types';

const SEVERITY_STYLES: Record<IncidentSeverity, string> = {
  3: 'bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300', // Low
  2: 'bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300', // Medium
  1: 'bg-orange-100 text-orange-800 dark:bg-orange-900/40 dark:text-orange-300', // High
  0: 'bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300', // Critical
};

export function SeverityBadge({ severity }: { severity: IncidentSeverity }) {
  return (
    <Badge className={cn('border-transparent', SEVERITY_STYLES[severity])}>
      {SEVERITY_LABELS[severity]}
    </Badge>
  );
}

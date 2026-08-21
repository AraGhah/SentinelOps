import { Badge } from '@/components/ui/badge';
import { STATUS_LABELS, type IncidentStatus } from '@/lib/types';

const RESOLVED: IncidentStatus = 5;

export function StatusBadge({ status }: { status: IncidentStatus }) {
  const variant = status === RESOLVED ? 'secondary' : 'outline';
  return <Badge variant={variant}>{STATUS_LABELS[status]}</Badge>;
}

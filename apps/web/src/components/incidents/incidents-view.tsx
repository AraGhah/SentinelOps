import Link from 'next/link';
import { ShieldAlert, ChevronLeft, ChevronRight } from 'lucide-react';
import type { Incident, PagedResult } from '@/lib/types';
import { EmptyState } from '@/components/states/empty-state';
import { SeverityBadge } from '@/components/incidents/severity-badge';
import { StatusBadge } from '@/components/incidents/status-badge';
import { Button } from '@/components/ui/button';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';

export function IncidentsView({ data }: { data: PagedResult<Incident> }) {
  if (data.items.length === 0 && data.page === 1) {
    return (
      <EmptyState
        icon={ShieldAlert}
        title="No incidents yet"
        description="Once a linked AWS account produces a GuardDuty or Security Hub finding, it will show up here."
      />
    );
  }

  const totalPages = Math.max(1, Math.ceil(data.totalCount / data.pageSize));

  return (
    <div className="space-y-3">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Title</TableHead>
            <TableHead>Severity</TableHead>
            <TableHead>Status</TableHead>
            <TableHead>Alerts</TableHead>
            <TableHead>Created</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {data.items.map((incident) => (
            <TableRow key={incident.id}>
              <TableCell className="font-medium">{incident.title}</TableCell>
              <TableCell>
                <SeverityBadge severity={incident.severity} />
              </TableCell>
              <TableCell>
                <StatusBadge status={incident.status} />
              </TableCell>
              <TableCell>{incident.alertCount}</TableCell>
              <TableCell>{new Date(incident.createdAtUtc).toLocaleString()}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>

      <div className="flex items-center justify-between">
        <p className="text-sm text-muted-foreground">
          Page {data.page} of {totalPages} &middot; {data.totalCount}{' '}
          {data.totalCount === 1 ? 'incident' : 'incidents'}
        </p>
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            disabled={data.page <= 1}
            render={
              data.page <= 1 ? undefined : <Link href={`/incidents?page=${data.page - 1}`} />
            }
          >
            <ChevronLeft />
            Previous
          </Button>
          <Button
            variant="outline"
            size="sm"
            disabled={data.page >= totalPages}
            render={
              data.page >= totalPages ? undefined : (
                <Link href={`/incidents?page=${data.page + 1}`} />
              )
            }
          >
            Next
            <ChevronRight />
          </Button>
        </div>
      </div>
    </div>
  );
}

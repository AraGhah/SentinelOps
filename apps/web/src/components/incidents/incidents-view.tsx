"use client";

import { useCallback, useEffect, useState } from "react";
import { ShieldAlert } from "lucide-react";
import { apiClient, ApiError } from "@/lib/api-client";
import type { Incident } from "@/lib/types";
import { LoadingState } from "@/components/states/loading-state";
import { EmptyState } from "@/components/states/empty-state";
import { ErrorState } from "@/components/states/error-state";
import { SeverityBadge } from "@/components/incidents/severity-badge";
import { StatusBadge } from "@/components/incidents/status-badge";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";

type ViewState =
  | { status: "loading" }
  | { status: "error"; message: string }
  | { status: "ready"; incidents: Incident[] };

export function IncidentsView() {
  const [state, setState] = useState<ViewState>({ status: "loading" });

  const load = useCallback(() => {
    setState({ status: "loading" });
    apiClient
      .get<Incident[]>("/api/v1/incidents")
      .then((incidents) => setState({ status: "ready", incidents }))
      .catch((error: unknown) => {
        const message = error instanceof ApiError ? error.detail ?? error.message : "Unexpected error";
        setState({ status: "error", message });
      });
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  if (state.status === "loading") {
    return <LoadingState rows={6} />;
  }

  if (state.status === "error") {
    return <ErrorState description={state.message} onRetry={load} />;
  }

  if (state.incidents.length === 0) {
    return (
      <EmptyState
        icon={ShieldAlert}
        title="No incidents yet"
        description="Once a linked AWS account produces a GuardDuty or Security Hub finding, it will show up here."
      />
    );
  }

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>Title</TableHead>
          <TableHead>Severity</TableHead>
          <TableHead>Status</TableHead>
          <TableHead>Assignee</TableHead>
          <TableHead>Created</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {state.incidents.map((incident) => (
          <TableRow key={incident.id}>
            <TableCell className="font-medium">{incident.title}</TableCell>
            <TableCell>
              <SeverityBadge severity={incident.severity} />
            </TableCell>
            <TableCell>
              <StatusBadge status={incident.status} />
            </TableCell>
            <TableCell>{incident.assignedUserEmail ?? "Unassigned"}</TableCell>
            <TableCell>{new Date(incident.createdAt).toLocaleString()}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

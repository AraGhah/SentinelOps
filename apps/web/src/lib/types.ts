// Mirrors apps/api/Domain/Incident.cs ordinals (serialized as numbers, no JsonStringEnumConverter). Do not reorder.
export type IncidentSeverity = 0 | 1 | 2 | 3;
export const SEVERITY_LABELS: Record<IncidentSeverity, string> = {
  0: 'Critical',
  1: 'High',
  2: 'Medium',
  3: 'Low',
};

export type IncidentStatus = 0 | 1 | 2 | 3 | 4 | 5 | 6;
export const STATUS_LABELS: Record<IncidentStatus, string> = {
  0: 'Triggered',
  1: 'Assigned',
  2: 'Acknowledged',
  3: 'Investigating',
  4: 'Monitoring',
  5: 'Resolved',
  6: 'Reopened',
};

// Matches apps/api/Incidents/Dtos.cs IncidentResponse.
export type Incident = {
  id: string;
  title: string;
  description: string | null;
  severity: IncidentSeverity;
  serviceId: string | null;
  assignedResponderUserId: string | null;
  status: IncidentStatus;
  alertCount: number;
  createdAtUtc: string;
  acknowledgedAtUtc: string | null;
  resolvedAtUtc: string | null;
  currentEscalationLevel: number | null;
};

// Matches apps/api/Common/PagedQuery.cs PagedResult<T>.
export type PagedResult<T> = {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
};

// Matches apps/api/Domain/OrganizationRole.cs. Higher value = more privilege; do not reorder.
export type OrganizationRole = 0 | 1 | 2 | 3;
export const ORGANIZATION_ROLE_LABELS: Record<OrganizationRole, string> = {
  0: 'Viewer',
  1: 'Responder',
  2: 'Administrator',
  3: 'Owner',
};

// Matches apps/api/Organizations/Dtos.cs MyOrganizationResponse.
export type Organization = {
  id: string;
  name: string;
  slug: string;
  role: OrganizationRole;
  isCurrent: boolean;
};

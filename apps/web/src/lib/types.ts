export type Severity = "Low" | "Medium" | "High" | "Critical";

export type IncidentStatus =
  | "New"
  | "Triaged"
  | "Investigating"
  | "Contained"
  | "Resolved"
  | "FalsePositive"
  | "Closed";

export type Incident = {
  id: string;
  title: string;
  severity: Severity;
  status: IncidentStatus;
  resourceArn: string;
  assignedUserEmail: string | null;
  createdAt: string;
};

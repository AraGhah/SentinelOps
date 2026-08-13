import type { Metadata } from 'next';
import { ShieldAlert, ShieldCheck, Clock, Cloud } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';

export const metadata: Metadata = {
  title: 'Dashboard — SentinelOps',
};

const stats = [
  { label: 'Open incidents', value: '—', icon: ShieldAlert },
  { label: 'Closed this week', value: '—', icon: ShieldCheck },
  { label: 'Mean time to close', value: '—', icon: Clock },
  { label: 'Linked AWS accounts', value: '—', icon: Cloud },
];

export default function DashboardPage() {
  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Dashboard</h1>
        <p className="text-sm text-muted-foreground">
          A real-time overview of security posture across your linked AWS accounts.
        </p>
      </div>
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {stats.map((stat) => (
          <Card key={stat.label}>
            <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
              <CardTitle className="text-sm font-medium text-muted-foreground">
                {stat.label}
              </CardTitle>
              <stat.icon className="size-4 text-muted-foreground" />
            </CardHeader>
            <CardContent>
              <div className="text-2xl font-bold">{stat.value}</div>
            </CardContent>
          </Card>
        ))}
      </div>
    </div>
  );
}

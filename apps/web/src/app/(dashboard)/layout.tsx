import { redirect } from 'next/navigation';
import { SidebarProvider, SidebarInset } from '@/components/ui/sidebar';
import { AppSidebar } from '@/components/layout/app-sidebar';
import { TopNav } from '@/components/layout/top-nav';
import { getSession } from '@/lib/auth/session';
import { listOrganizations } from '@/lib/auth/actions';

export default async function DashboardLayout({ children }: { children: React.ReactNode }) {
  const session = await getSession();
  if (!session) {
    redirect('/login');
  }

  const organizations = await listOrganizations();

  return (
    <SidebarProvider>
      <AppSidebar />
      <SidebarInset>
        <TopNav
          userEmail={session.email}
          organizations={organizations}
          activeOrganizationId={session.organizationId}
        />
        <div className="flex flex-1 flex-col gap-4 p-4 md:p-6">{children}</div>
      </SidebarInset>
    </SidebarProvider>
  );
}

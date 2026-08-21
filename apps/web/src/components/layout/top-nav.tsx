'use client';

import { useTransition } from 'react';
import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { Bell, Check, ChevronsUpDown, LogOut, User } from 'lucide-react';
import { toast } from 'sonner';
import { SidebarTrigger } from '@/components/ui/sidebar';
import { Separator } from '@/components/ui/separator';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbList,
  BreadcrumbPage,
} from '@/components/ui/breadcrumb';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { Avatar, AvatarFallback } from '@/components/ui/avatar';
import { navItems } from '@/components/layout/nav-config';
import { logoutAction, switchOrganization } from '@/lib/auth/actions';
import type { Organization } from '@/lib/types';
import { cn } from '@/lib/utils';

function pageTitle(pathname: string) {
  return navItems.find((item) => pathname.startsWith(item.href))?.title ?? 'SentinelOps';
}

function OrganizationSwitcher({
  organizations,
  activeOrganizationId,
}: {
  organizations: Organization[];
  activeOrganizationId: string | null;
}) {
  const [isPending, startTransition] = useTransition();
  const active = organizations.find((org) => org.id === activeOrganizationId) ?? organizations[0];

  if (organizations.length === 0) {
    return null;
  }

  // Nothing to switch between — just show the name, no dropdown needed.
  if (organizations.length === 1) {
    return (
      <span className="hidden max-w-40 truncate text-sm font-medium text-muted-foreground sm:inline">
        {active.name}
      </span>
    );
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        render={
          <Button variant="outline" size="sm" className="max-w-48 gap-1.5" disabled={isPending}>
            <span className="truncate">{active?.name ?? 'Select organization'}</span>
            <ChevronsUpDown className="size-3.5 text-muted-foreground" />
          </Button>
        }
      />
      <DropdownMenuContent align="start" className="w-56">
        <DropdownMenuLabel>Organizations</DropdownMenuLabel>
        <DropdownMenuSeparator />
        {organizations.map((org) => (
          <DropdownMenuItem
            key={org.id}
            onSelect={() => {
              if (org.id === activeOrganizationId) return;
              startTransition(async () => {
                const result = await switchOrganization(org.id);
                if (result?.error) {
                  toast.error(result.error);
                }
              });
            }}
          >
            <Check
              className={cn('size-4', org.id === activeOrganizationId ? 'opacity-100' : 'opacity-0')}
            />
            <span className="truncate">{org.name}</span>
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

export function TopNav({
  userEmail,
  organizations,
  activeOrganizationId,
}: {
  userEmail: string;
  organizations: Organization[];
  activeOrganizationId: string | null;
}) {
  const pathname = usePathname();
  const initials = userEmail.slice(0, 2).toUpperCase();
  const [, startTransition] = useTransition();

  return (
    <header className="flex h-14 shrink-0 items-center gap-2 border-b px-4">
      <SidebarTrigger />
      <Separator orientation="vertical" className="h-5" />
      <Breadcrumb>
        <BreadcrumbList>
          <BreadcrumbItem>
            <BreadcrumbPage>{pageTitle(pathname)}</BreadcrumbPage>
          </BreadcrumbItem>
        </BreadcrumbList>
      </Breadcrumb>

      <OrganizationSwitcher organizations={organizations} activeOrganizationId={activeOrganizationId} />

      <div className="ml-auto flex items-center gap-2">
        {/* Search isn't implemented yet — disabled rather than a fake input
            that looks functional but does nothing. */}
        <div className="relative hidden sm:block">
          <Input
            placeholder="Search (coming soon)"
            disabled
            className="h-8 w-56 opacity-60"
            title="Search isn't available yet"
          />
        </div>
        {/* Notifications aren't implemented on the frontend yet either
            (no consumer for the dashboard-broadcast websocket). */}
        <Button
          variant="ghost"
          size="icon"
          className="size-8 opacity-60"
          aria-label="Notifications (coming soon)"
          disabled
          title="Notifications aren't available yet"
        >
          <Bell className="size-4" />
        </Button>
        <DropdownMenu>
          <DropdownMenuTrigger
            render={
              <Button variant="ghost" size="icon" className="size-8 rounded-full">
                <Avatar className="size-8">
                  <AvatarFallback className="text-xs">{initials}</AvatarFallback>
                </Avatar>
              </Button>
            }
          />
          <DropdownMenuContent align="end" className="w-56">
            <DropdownMenuLabel className="flex flex-col">
              <span className="font-medium">Signed in as</span>
              <span className="truncate text-xs text-muted-foreground">{userEmail}</span>
            </DropdownMenuLabel>
            <DropdownMenuSeparator />
            <DropdownMenuItem render={<Link href="/settings" />}>
              <User />
              Profile &amp; settings
            </DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuItem
              variant="destructive"
              onSelect={() => startTransition(() => logoutAction())}
            >
              <LogOut />
              Log out
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </header>
  );
}

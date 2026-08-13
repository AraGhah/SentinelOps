import type { LucideIcon } from 'lucide-react';
import { LayoutDashboard, ShieldAlert, Activity, Cloud, Users, Settings } from 'lucide-react';

export type NavItem = {
  title: string;
  href: string;
  icon: LucideIcon;
};

export const navItems: NavItem[] = [
  { title: 'Dashboard', href: '/dashboard', icon: LayoutDashboard },
  { title: 'Incidents', href: '/incidents', icon: ShieldAlert },
  { title: 'Events', href: '/events', icon: Activity },
  { title: 'AWS Accounts', href: '/accounts', icon: Cloud },
  { title: 'Users', href: '/users', icon: Users },
  { title: 'Settings', href: '/settings', icon: Settings },
];

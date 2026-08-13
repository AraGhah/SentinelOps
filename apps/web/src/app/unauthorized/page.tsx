import type { Metadata } from 'next';
import Link from 'next/link';
import { LockKeyhole } from 'lucide-react';
import { buttonVariants } from '@/components/ui/button';
import { cn } from '@/lib/utils';

export const metadata: Metadata = {
  title: 'Sign in required — SentinelOps',
};

export default function UnauthorizedPage() {
  return (
    <div className="flex min-h-screen flex-col items-center justify-center gap-3 bg-muted/40 p-4 text-center">
      <LockKeyhole className="size-10 text-muted-foreground" />
      <h1 className="text-xl font-semibold tracking-tight">Sign in required</h1>
      <p className="max-w-sm text-sm text-muted-foreground">
        You need to sign in to view this page.
      </p>
      <Link href="/login" className={cn(buttonVariants({ variant: 'default' }), 'mt-2')}>
        Go to sign in
      </Link>
    </div>
  );
}

import type { Metadata } from 'next';
import { redirect } from 'next/navigation';
import { MfaForm } from '@/components/auth/mfa-form';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { getMfaChallenge } from '@/lib/auth/session';

export const metadata: Metadata = {
  title: 'Two-factor authentication — SentinelOps',
};

export default async function LoginMfaPage({
  searchParams,
}: {
  searchParams: Promise<{ email?: string }>;
}) {
  const { email: emailParam } = await searchParams;

  // The challenge session token itself lives only in the short-lived
  // httpOnly mfa_challenge cookie (set by the login action) — never in the
  // URL. No cookie means there's no pending MFA challenge to complete.
  const challenge = await getMfaChallenge();
  if (!challenge) {
    redirect('/login');
  }

  const email = emailParam ?? challenge.email;

  return (
    <div className="flex min-h-screen items-center justify-center bg-muted/40 p-4">
      <Card className="w-full max-w-sm">
        <CardHeader>
          <CardTitle className="text-xl">Two-factor authentication</CardTitle>
          <CardDescription>Enter the code from your authenticator app.</CardDescription>
        </CardHeader>
        <CardContent>
          <MfaForm email={email} />
        </CardContent>
      </Card>
    </div>
  );
}

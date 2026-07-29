import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { MfaForm } from "@/components/auth/mfa-form";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

export const metadata: Metadata = {
  title: "Two-factor authentication — SentinelOps",
};

export default async function LoginMfaPage({
  searchParams,
}: {
  searchParams: Promise<{ email?: string; session?: string }>;
}) {
  const { email, session } = await searchParams;

  if (!email || !session) {
    redirect("/login");
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-muted/40 p-4">
      <Card className="w-full max-w-sm">
        <CardHeader>
          <CardTitle className="text-xl">Two-factor authentication</CardTitle>
          <CardDescription>Enter the code from your authenticator app.</CardDescription>
        </CardHeader>
        <CardContent>
          <MfaForm email={email} challengeSession={session} />
        </CardContent>
      </Card>
    </div>
  );
}

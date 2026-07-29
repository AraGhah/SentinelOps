import type { Metadata } from "next";
import { VerifyEmailForm } from "@/components/auth/verify-email-form";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

export const metadata: Metadata = {
  title: "Verify email — SentinelOps",
};

export default async function VerifyEmailPage({
  searchParams,
}: {
  searchParams: Promise<{ email?: string }>;
}) {
  const { email } = await searchParams;

  return (
    <div className="flex min-h-screen items-center justify-center bg-muted/40 p-4">
      <Card className="w-full max-w-sm">
        <CardHeader>
          <CardTitle className="text-xl">Verify your email</CardTitle>
          <CardDescription>
            We sent a verification code to your email address. Enter it below to activate your
            account.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <VerifyEmailForm email={email ?? ""} />
        </CardContent>
      </Card>
    </div>
  );
}

"use client";

import { useState, useTransition } from "react";
import { toast } from "sonner";
import { startMfaSetup, enableMfa } from "@/lib/auth/actions";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";

type SetupState = { secretCode: string; otpAuthUri: string } | null;

export function SecuritySettings() {
  const [setup, setSetup] = useState<SetupState>(null);
  const [code, setCode] = useState("");
  const [enabled, setEnabled] = useState(false);
  const [isStarting, startSetupTransition] = useTransition();
  const [isConfirming, startConfirmTransition] = useTransition();

  function onStart() {
    startSetupTransition(async () => {
      const result = await startMfaSetup();
      if ("error" in result) {
        toast.error(result.error);
        return;
      }
      setSetup(result);
    });
  }

  function onConfirm() {
    startConfirmTransition(async () => {
      const result = await enableMfa(code);
      if (result?.error) {
        toast.error(result.error);
        return;
      }
      toast.success("Authenticator app enabled.");
      setEnabled(true);
      setSetup(null);
    });
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Two-factor authentication</CardTitle>
        <CardDescription>
          Add an authenticator app (Google Authenticator, Authy, 1Password, etc.) as a second
          sign-in factor.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {enabled ? (
          <p className="text-sm text-muted-foreground">
            Authenticator app is enabled on your account.
          </p>
        ) : !setup ? (
          <Button onClick={onStart} disabled={isStarting}>
            {isStarting ? "Starting..." : "Enable authenticator app"}
          </Button>
        ) : (
          <div className="space-y-3">
            <p className="text-sm text-muted-foreground">
              Enter this key into your authenticator app, then confirm the 6-digit code it
              generates.
            </p>
            <div className="rounded-md border bg-muted/40 p-3 font-mono text-sm break-all">
              {setup.secretCode}
            </div>
            <div className="space-y-2">
              <Label htmlFor="mfa-code">Confirmation code</Label>
              <Input
                id="mfa-code"
                inputMode="numeric"
                placeholder="123456"
                value={code}
                onChange={(event) => setCode(event.target.value)}
              />
            </div>
            <Button onClick={onConfirm} disabled={isConfirming || code.length === 0}>
              {isConfirming ? "Confirming..." : "Confirm and enable"}
            </Button>
          </div>
        )}
      </CardContent>
    </Card>
  );
}

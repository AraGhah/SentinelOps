"use client";

import { useTransition } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { toast } from "sonner";
import { verifyMfa } from "@/lib/auth/actions";
import { mfaCodeSchema, type MfaCodeInput } from "@/lib/validations/auth";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";

export function MfaForm({ email, challengeSession }: { email: string; challengeSession: string }) {
  const [isPending, startTransition] = useTransition();

  const form = useForm<MfaCodeInput>({
    resolver: zodResolver(mfaCodeSchema),
    defaultValues: { code: "" },
  });

  function onSubmit(values: MfaCodeInput) {
    startTransition(async () => {
      const result = await verifyMfa({ email, code: values.code, challengeSession });
      if (result?.error) {
        toast.error(result.error);
      }
    });
  }

  return (
    <Form {...form}>
      <form onSubmit={form.handleSubmit(onSubmit)} className="space-y-4">
        <FormField
          control={form.control}
          name="code"
          render={({ field }) => (
            <FormItem>
              <FormLabel>Authenticator code</FormLabel>
              <FormControl>
                <Input
                  inputMode="numeric"
                  placeholder="123456"
                  autoComplete="one-time-code"
                  autoFocus
                  {...field}
                />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />
        <Button type="submit" className="w-full" disabled={isPending}>
          {isPending ? "Verifying..." : "Verify"}
        </Button>
      </form>
    </Form>
  );
}

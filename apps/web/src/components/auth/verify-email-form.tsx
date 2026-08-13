'use client';

import { useTransition } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { toast } from 'sonner';
import { confirmEmail, resendConfirmationCode } from '@/lib/auth/actions';
import { confirmEmailSchema, type ConfirmEmailInput } from '@/lib/validations/auth';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from '@/components/ui/form';

export function VerifyEmailForm({ email }: { email: string }) {
  const [isPending, startTransition] = useTransition();
  const [isResending, startResendTransition] = useTransition();

  const form = useForm<ConfirmEmailInput>({
    resolver: zodResolver(confirmEmailSchema),
    defaultValues: { email, code: '' },
  });

  function onSubmit(values: ConfirmEmailInput) {
    startTransition(async () => {
      const result = await confirmEmail(values);
      if (result?.error) {
        toast.error(result.error);
      }
    });
  }

  function onResend() {
    startResendTransition(async () => {
      const result = await resendConfirmationCode({ email: form.getValues('email') });
      if (result?.error) {
        toast.error(result.error);
      } else {
        toast.success('A new verification code has been sent.');
      }
    });
  }

  return (
    <Form {...form}>
      <form onSubmit={form.handleSubmit(onSubmit)} className="space-y-4">
        <FormField
          control={form.control}
          name="email"
          render={({ field }) => (
            <FormItem>
              <FormLabel>Email</FormLabel>
              <FormControl>
                <Input type="email" autoComplete="email" {...field} />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />
        <FormField
          control={form.control}
          name="code"
          render={({ field }) => (
            <FormItem>
              <FormLabel>Verification code</FormLabel>
              <FormControl>
                <Input
                  inputMode="numeric"
                  placeholder="123456"
                  autoComplete="one-time-code"
                  {...field}
                />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />
        <Button type="submit" className="w-full" disabled={isPending}>
          {isPending ? 'Verifying...' : 'Verify email'}
        </Button>
        <Button
          type="button"
          variant="ghost"
          className="w-full"
          disabled={isResending}
          onClick={onResend}
        >
          {isResending ? 'Sending...' : 'Resend code'}
        </Button>
      </form>
    </Form>
  );
}

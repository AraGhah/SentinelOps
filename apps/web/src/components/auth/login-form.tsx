'use client';

import { useTransition } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { toast } from 'sonner';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { login } from '@/lib/auth/actions';
import { loginSchema, type LoginInput } from '@/lib/validations/auth';
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

export function LoginForm() {
  const [isPending, startTransition] = useTransition();
  const router = useRouter();

  const form = useForm<LoginInput>({
    resolver: zodResolver(loginSchema),
    defaultValues: { email: '', password: '' },
  });

  function onSubmit(values: LoginInput) {
    startTransition(async () => {
      const result = await login(values);
      if (!result) return;

      if ('mfaRequired' in result) {
        // The challenge session token lives server-side in a short-lived
        // httpOnly cookie (set by the login action) — only the email, which
        // isn't sensitive, goes in the URL for display/prefill purposes.
        router.push(`/login/mfa?email=${encodeURIComponent(result.email)}`);
        return;
      }

      if (result.error) {
        toast.error(result.error);
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
                <Input type="email" placeholder="you@company.com" autoComplete="email" {...field} />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />
        <FormField
          control={form.control}
          name="password"
          render={({ field }) => (
            <FormItem>
              <FormLabel>Password</FormLabel>
              <FormControl>
                <Input
                  type="password"
                  placeholder="••••••••"
                  autoComplete="current-password"
                  {...field}
                />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />
        <Button type="submit" className="w-full" disabled={isPending}>
          {isPending ? 'Signing in...' : 'Sign in'}
        </Button>
        <div className="flex items-center justify-between text-sm text-muted-foreground">
          <Link href="/forgot-password" className="underline underline-offset-4">
            Forgot password?
          </Link>
          <Link href="/register" className="underline underline-offset-4">
            Create account
          </Link>
        </div>
      </form>
    </Form>
  );
}

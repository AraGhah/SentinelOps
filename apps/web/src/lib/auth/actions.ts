'use server';

import { redirect } from 'next/navigation';
import { revalidatePath } from 'next/cache';
import { apiClient, ApiError } from '@/lib/api-client';
import {
  loginSchema,
  registerSchema,
  confirmEmailSchema,
  resendCodeSchema,
  forgotPasswordSchema,
  resetPasswordSchema,
  mfaCodeSchema,
} from '@/lib/validations/auth';
import {
  clearMfaChallenge,
  clearSession,
  getMfaChallenge,
  setMfaChallenge,
  setSession,
  type Session,
} from '@/lib/auth/session';
import type { Organization } from '@/lib/types';

type TokenSet = {
  accessToken: string;
  idToken: string;
  refreshToken: string;
  expiresIn: number;
};

type MfaChallenge = {
  session: string;
  email: string;
};

type LoginResponse = {
  tokens: TokenSet | null;
  mfaChallenge: MfaChallenge | null;
};

export type ActionResult = { error: string } | void;
// Deliberately does NOT carry the Cognito challenge session token — that's
// stored server-side in a short-lived httpOnly cookie (see FE-03 / setMfaChallenge)
// and never sent to the client.
export type LoginResult = { error: string } | { mfaRequired: true; email: string } | void;

function toSession(email: string, tokens: TokenSet): Session {
  return {
    accessToken: tokens.accessToken,
    idToken: tokens.idToken,
    refreshToken: tokens.refreshToken,
    email,
    organizationId: null,
    expiresAt: Date.now() + tokens.expiresIn * 1000,
  };
}

// Resolves which organization should be active for a freshly-issued session:
// prefers whatever the backend already considers "current" for this user
// (see OrganizationsController.ListMine), falling back to the first org the
// user belongs to. Returns null if the user isn't a member of any org yet.
async function resolveActiveOrganizationId(accessToken: string): Promise<string | null> {
  try {
    const organizations = await apiClient.get<Organization[]>('/api/v1/organizations', {
      token: accessToken,
    });
    const active = organizations.find((org) => org.isCurrent) ?? organizations[0];
    return active?.id ?? null;
  } catch {
    return null;
  }
}

async function buildSession(email: string, tokens: TokenSet): Promise<Session> {
  const session = toSession(email, tokens);
  session.organizationId = await resolveActiveOrganizationId(session.accessToken);
  return session;
}

function errorMessage(error: unknown, fallback: string): string {
  if (error instanceof ApiError) {
    return error.detail ?? error.message;
  }
  return fallback;
}

export async function register(values: {
  email: string;
  password: string;
  confirmPassword: string;
}): Promise<ActionResult> {
  const parsed = registerSchema.safeParse(values);
  if (!parsed.success) {
    return { error: parsed.error.issues[0]?.message ?? 'Invalid details' };
  }

  try {
    await apiClient.post('/api/v1/auth/register', {
      email: parsed.data.email,
      password: parsed.data.password,
    });
  } catch (error) {
    return { error: errorMessage(error, 'Something went wrong. Please try again.') };
  }

  redirect(`/verify-email?email=${encodeURIComponent(parsed.data.email)}`);
}

export async function confirmEmail(values: { email: string; code: string }): Promise<ActionResult> {
  const parsed = confirmEmailSchema.safeParse(values);
  if (!parsed.success) {
    return { error: parsed.error.issues[0]?.message ?? 'Invalid code' };
  }

  try {
    await apiClient.post('/api/v1/auth/confirm-email', parsed.data);
  } catch (error) {
    return { error: errorMessage(error, 'Could not verify your email. Please try again.') };
  }

  redirect('/login?verified=1');
}

export async function resendConfirmationCode(values: { email: string }): Promise<ActionResult> {
  const parsed = resendCodeSchema.safeParse(values);
  if (!parsed.success) {
    return { error: parsed.error.issues[0]?.message ?? 'Invalid email' };
  }

  try {
    await apiClient.post('/api/v1/auth/resend-confirmation', parsed.data);
  } catch (error) {
    return { error: errorMessage(error, 'Could not resend the code. Please try again.') };
  }
}

export async function login(values: { email: string; password: string }): Promise<LoginResult> {
  const parsed = loginSchema.safeParse(values);

  if (!parsed.success) {
    return { error: parsed.error.issues[0]?.message ?? 'Invalid credentials' };
  }

  const { email, password } = parsed.data;
  let response: LoginResponse;

  try {
    response = await apiClient.post<LoginResponse>('/api/v1/auth/login', { email, password });
  } catch (error) {
    return { error: errorMessage(error, 'Something went wrong. Please try again.') };
  }

  if (response.mfaChallenge) {
    // Store the challenge server-side; the client only ever learns the
    // email (needed to display/prefill the MFA form), never the session
    // token itself. See FE-03.
    await setMfaChallenge({
      email: response.mfaChallenge.email,
      challengeSession: response.mfaChallenge.session,
    });
    return { mfaRequired: true, email: response.mfaChallenge.email };
  }

  if (!response.tokens) {
    return { error: 'Sign-in failed. Please try again.' };
  }

  await setSession(await buildSession(email, response.tokens));
  redirect('/dashboard');
}

export async function verifyMfa(values: { email: string; code: string }): Promise<ActionResult> {
  const parsed = mfaCodeSchema.safeParse({ code: values.code });
  if (!parsed.success) {
    return { error: parsed.error.issues[0]?.message ?? 'Invalid code' };
  }

  const challenge = await getMfaChallenge();
  if (!challenge || challenge.email !== values.email) {
    await clearMfaChallenge();
    return { error: 'Your sign-in session has expired. Please sign in again.' };
  }

  let tokens: TokenSet;
  try {
    tokens = await apiClient.post<TokenSet>('/api/v1/auth/mfa/verify', {
      email: values.email,
      code: values.code,
      session: challenge.challengeSession,
    });
  } catch (error) {
    // Cleared on failure too (not just success) — a stale/rejected challenge
    // session shouldn't be retried silently; the user re-enters credentials.
    await clearMfaChallenge();
    return { error: errorMessage(error, 'Invalid authentication code.') };
  }

  await clearMfaChallenge();
  await setSession(await buildSession(values.email, tokens));
  redirect('/dashboard');
}

export async function forgotPassword(values: { email: string }): Promise<ActionResult> {
  const parsed = forgotPasswordSchema.safeParse(values);
  if (!parsed.success) {
    return { error: parsed.error.issues[0]?.message ?? 'Invalid email' };
  }

  try {
    await apiClient.post('/api/v1/auth/forgot-password', parsed.data);
  } catch (error) {
    return { error: errorMessage(error, 'Something went wrong. Please try again.') };
  }

  redirect(`/reset-password?email=${encodeURIComponent(parsed.data.email)}`);
}

export async function resetPassword(values: {
  email: string;
  code: string;
  newPassword: string;
  confirmPassword: string;
}): Promise<ActionResult> {
  const parsed = resetPasswordSchema.safeParse(values);
  if (!parsed.success) {
    return { error: parsed.error.issues[0]?.message ?? 'Invalid details' };
  }

  try {
    await apiClient.post('/api/v1/auth/reset-password', {
      email: parsed.data.email,
      code: parsed.data.code,
      newPassword: parsed.data.newPassword,
    });
  } catch (error) {
    return { error: errorMessage(error, 'Could not reset your password. Please try again.') };
  }

  redirect('/login?reset=1');
}

// Called by getValidSession() when the access token is near/past expiry.
// Returns the refreshed session, or null (and clears the cookie) if the
// refresh token itself is no longer valid.
export async function refreshSession(session: Session): Promise<Session | null> {
  try {
    const tokens = await apiClient.post<TokenSet>('/api/v1/auth/refresh', {
      refreshToken: session.refreshToken,
      email: session.email,
    });
    // Keep the already-resolved active org — no need to re-fetch the
    // organization list on every silent token refresh.
    const next: Session = { ...toSession(session.email, tokens), organizationId: session.organizationId };
    await setSession(next);
    return next;
  } catch {
    await clearSession();
    return null;
  }
}

export type MfaSetupResult = { secretCode: string; otpAuthUri: string } | { error: string };

export async function startMfaSetup(): Promise<MfaSetupResult> {
  const { getValidSession } = await import('@/lib/auth/session');
  const session = await getValidSession();
  if (!session) return { error: 'Your session has expired. Please sign in again.' };

  try {
    return await apiClient.get<{ secretCode: string; otpAuthUri: string }>(
      '/api/v1/auth/mfa/setup',
      {
        token: session.accessToken,
      },
    );
  } catch (error) {
    return { error: errorMessage(error, 'Could not start MFA setup. Please try again.') };
  }
}

export async function enableMfa(code: string): Promise<ActionResult> {
  const { getValidSession } = await import('@/lib/auth/session');
  const session = await getValidSession();
  if (!session) return { error: 'Your session has expired. Please sign in again.' };

  try {
    await apiClient.post('/api/v1/auth/mfa/enable', { code }, { token: session.accessToken });
  } catch (error) {
    return { error: errorMessage(error, 'Invalid code. Please try again.') };
  }
}

// Lists every organization the signed-in user belongs to — backs the
// minimal org switcher in the top nav (see FE-02).
export async function listOrganizations(): Promise<Organization[]> {
  const { getValidSession } = await import('@/lib/auth/session');
  const session = await getValidSession();
  if (!session) return [];

  try {
    return await apiClient.get<Organization[]>('/api/v1/organizations', {
      token: session.accessToken,
    });
  } catch {
    return [];
  }
}

// Switches the active organization: tells the backend (so it's remembered
// as the user's LastActiveOrganizationId server-side too) and updates the
// local session cookie so subsequent org-scoped calls use it immediately.
export async function switchOrganization(organizationId: string): Promise<ActionResult> {
  const { getValidSession, setActiveOrganization } = await import('@/lib/auth/session');
  const session = await getValidSession();
  if (!session) return { error: 'Your session has expired. Please sign in again.' };

  try {
    await apiClient.post(`/api/v1/organizations/${organizationId}/switch`, undefined, {
      token: session.accessToken,
    });
  } catch (error) {
    return { error: errorMessage(error, 'Could not switch organization. Please try again.') };
  }

  await setActiveOrganization(organizationId);
  revalidatePath('/', 'layout');
}

export async function logoutAction(): Promise<void> {
  const { getSession } = await import('@/lib/auth/session');
  const session = await getSession();

  if (session) {
    try {
      await apiClient.post('/api/v1/auth/logout', { accessToken: session.accessToken });
    } catch {
      // Best-effort — the local session is cleared regardless.
    }
  }

  await clearSession();
  redirect('/login');
}

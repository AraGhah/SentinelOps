import 'server-only';
import { cookies } from 'next/headers';
import { MFA_CHALLENGE_COOKIE_NAME, SESSION_COOKIE_NAME } from '@/lib/auth/constants';

export type Session = {
  accessToken: string;
  idToken: string;
  refreshToken: string;
  email: string;
  // Active organization for org-scoped API calls (e.g.
  // /api/v1/organizations/{organizationId}/incidents). Null if the user
  // doesn't belong to any organization yet.
  organizationId: string | null;
  expiresAt: number; // epoch ms
};

// Pending MFA challenge, stored in its own short-lived cookie instead of a
// URL query string (see FE-03) between login submission and MFA verify.
export type MfaChallengeCookie = {
  email: string;
  challengeSession: string;
};

const REFRESH_SKEW_MS = 60_000;

export async function getSession(): Promise<Session | null> {
  const cookieStore = await cookies();
  const raw = cookieStore.get(SESSION_COOKIE_NAME)?.value;
  if (!raw) return null;

  try {
    return JSON.parse(raw) as Session;
  } catch {
    return null;
  }
}

// Returns a session with a non-expired access token, transparently refreshing
// it against the API when it's within a minute of expiring (or already past).
export async function getValidSession(): Promise<Session | null> {
  const session = await getSession();
  if (!session) return null;

  if (Date.now() < session.expiresAt - REFRESH_SKEW_MS) {
    return session;
  }

  const { refreshSession } = await import('@/lib/auth/actions');
  return refreshSession(session);
}

export async function setSession(session: Session): Promise<void> {
  const cookieStore = await cookies();
  cookieStore.set(SESSION_COOKIE_NAME, JSON.stringify(session), {
    httpOnly: true,
    secure: process.env.NODE_ENV === 'production',
    sameSite: 'lax',
    path: '/',
    maxAge: 60 * 60 * 24 * 30,
  });
}

export async function clearSession(): Promise<void> {
  const cookieStore = await cookies();
  cookieStore.delete(SESSION_COOKIE_NAME);
}

// Updates just the active organization on the existing session cookie. Used
// by the org switcher (see switchOrganization in lib/auth/actions.ts).
export async function setActiveOrganization(organizationId: string): Promise<void> {
  const session = await getSession();
  if (!session) return;
  await setSession({ ...session, organizationId });
}

export async function getMfaChallenge(): Promise<MfaChallengeCookie | null> {
  const cookieStore = await cookies();
  const raw = cookieStore.get(MFA_CHALLENGE_COOKIE_NAME)?.value;
  if (!raw) return null;

  try {
    return JSON.parse(raw) as MfaChallengeCookie;
  } catch {
    return null;
  }
}

export async function setMfaChallenge(challenge: MfaChallengeCookie): Promise<void> {
  const cookieStore = await cookies();
  cookieStore.set(MFA_CHALLENGE_COOKIE_NAME, JSON.stringify(challenge), {
    httpOnly: true,
    secure: process.env.NODE_ENV === 'production',
    sameSite: 'lax',
    path: '/',
    maxAge: 60 * 5, // 5 minutes — matches Cognito's own challenge session lifetime ballpark.
  });
}

export async function clearMfaChallenge(): Promise<void> {
  const cookieStore = await cookies();
  cookieStore.delete(MFA_CHALLENGE_COOKIE_NAME);
}

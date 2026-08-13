import 'server-only';
import { cookies } from 'next/headers';
import { SESSION_COOKIE_NAME } from '@/lib/auth/constants';

export type Session = {
  accessToken: string;
  idToken: string;
  refreshToken: string;
  email: string;
  expiresAt: number; // epoch ms
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

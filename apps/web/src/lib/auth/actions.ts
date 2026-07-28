"use server";

import { redirect } from "next/navigation";
import { apiClient, ApiError } from "@/lib/api-client";
import { loginSchema } from "@/lib/validations/auth";
import { clearSession, setSession } from "@/lib/auth/session";

type LoginResponse = {
  token: string;
};

export type LoginResult = { error: string } | void;

// Calls the real endpoint first; falls back to a local stub session only when
// the API is unreachable (status 0), so the frontend works standalone before
// apps/api ships auth, and switches over automatically once it does.
export async function login(values: { email: string; password: string }): Promise<LoginResult> {
  const parsed = loginSchema.safeParse(values);

  if (!parsed.success) {
    return { error: parsed.error.issues[0]?.message ?? "Invalid credentials" };
  }

  const { email, password } = parsed.data;

  try {
    const { token } = await apiClient.post<LoginResponse>("/api/v1/auth/login", {
      email,
      password,
    });
    await setSession({ token, email });
  } catch (error) {
    if (error instanceof ApiError && error.status === 0) {
      await setSession({ token: `stub.${email}`, email });
    } else if (error instanceof ApiError) {
      return { error: error.detail ?? error.message };
    } else {
      return { error: "Something went wrong. Please try again." };
    }
  }

  redirect("/dashboard");
}

export async function logoutAction(): Promise<void> {
  await clearSession();
  redirect("/login");
}

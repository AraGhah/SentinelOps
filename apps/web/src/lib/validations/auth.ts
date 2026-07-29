import { z } from "zod";

const email = z.string().min(1, "Email is required").email("Enter a valid email address");
const password = z.string().min(8, "Password must be at least 8 characters");
const code = z.string().min(6, "Enter the 6-digit code").max(6, "Enter the 6-digit code");

export const loginSchema = z.object({
  email,
  password,
});
export type LoginInput = z.infer<typeof loginSchema>;

export const registerSchema = z
  .object({
    email,
    password,
    confirmPassword: z.string().min(1, "Confirm your password"),
  })
  .refine((data) => data.password === data.confirmPassword, {
    message: "Passwords do not match",
    path: ["confirmPassword"],
  });
export type RegisterInput = z.infer<typeof registerSchema>;

export const confirmEmailSchema = z.object({
  email,
  code,
});
export type ConfirmEmailInput = z.infer<typeof confirmEmailSchema>;

export const resendCodeSchema = z.object({
  email,
});
export type ResendCodeInput = z.infer<typeof resendCodeSchema>;

export const forgotPasswordSchema = z.object({
  email,
});
export type ForgotPasswordInput = z.infer<typeof forgotPasswordSchema>;

export const resetPasswordSchema = z
  .object({
    email,
    code,
    newPassword: password,
    confirmPassword: z.string().min(1, "Confirm your password"),
  })
  .refine((data) => data.newPassword === data.confirmPassword, {
    message: "Passwords do not match",
    path: ["confirmPassword"],
  });
export type ResetPasswordInput = z.infer<typeof resetPasswordSchema>;

export const mfaCodeSchema = z.object({
  code,
});
export type MfaCodeInput = z.infer<typeof mfaCodeSchema>;

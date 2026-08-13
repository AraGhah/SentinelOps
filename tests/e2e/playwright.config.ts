import { defineConfig } from "@playwright/test";

// No hardcoded domain — targets whatever the caller points it at via env
// vars (read directly in specs/smoke.spec.ts). Locally that's docker-
// compose's http://localhost:5000 / :3000; in CI's cd.yml it's the AWS-
// generated API Gateway / CloudFront hostnames of whichever environment was
// just deployed (there's no real custom domain yet — see
// docs/security/security-assumptions.md).
export default defineConfig({
  testDir: "./specs",
  timeout: 30_000,
  retries: process.env.CI ? 2 : 0,
  reporter: process.env.CI ? "github" : "list",
});

import { expect, test } from "@playwright/test";

// Smoke test, not the full end-to-end suite (register/login/create-org/
// send-alert/... — that's checklist item 29's job, a much larger separate
// task). This just confirms a deployed environment is actually up: the API
// answers its health check and the frontend serves its home page.
const apiUrl = process.env.E2E_API_URL ?? "http://localhost:5000";
const frontendUrl = process.env.E2E_FRONTEND_URL ?? "http://localhost:3000";

test("API health check responds 200", async ({ request }) => {
  const response = await request.get(`${apiUrl}/healthz`);
  expect(response.status()).toBe(200);
});

test("frontend home page loads", async ({ page }) => {
  const response = await page.goto(frontendUrl);
  expect(response?.status()).toBe(200);
  await expect(page).toHaveTitle(/SentinelOps/i);
});

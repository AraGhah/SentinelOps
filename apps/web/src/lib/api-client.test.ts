import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// api-client.ts reads NEXT_PUBLIC_API_URL once, at module-load time — each
// test needs a fresh module instance (vi.resetModules) to exercise both the
// fallback and the env-var-override paths.
describe('apiClient base URL resolution', () => {
  const originalEnv = process.env.NEXT_PUBLIC_API_URL;

  beforeEach(() => {
    vi.resetModules();
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ ok: true }), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          }),
      ),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    process.env.NEXT_PUBLIC_API_URL = originalEnv;
  });

  it('falls back to http://localhost:5000 when NEXT_PUBLIC_API_URL is unset', async () => {
    delete process.env.NEXT_PUBLIC_API_URL;
    const { apiClient } = await import('./api-client');

    await apiClient.get('/healthz');

    expect(fetch).toHaveBeenCalledWith('http://localhost:5000/healthz', expect.anything());
  });

  it("uses NEXT_PUBLIC_API_URL when it's set", async () => {
    process.env.NEXT_PUBLIC_API_URL = 'https://api-dev.sentinelops.example';
    const { apiClient } = await import('./api-client');

    await apiClient.get('/healthz');

    expect(fetch).toHaveBeenCalledWith(
      'https://api-dev.sentinelops.example/healthz',
      expect.anything(),
    );
  });
});

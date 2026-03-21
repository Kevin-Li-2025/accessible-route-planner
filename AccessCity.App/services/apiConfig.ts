/**
 * Pure URL resolution for the REST API (no React Native imports). Used by api.ts and unit-tested in Node.
 */
export type PlatformOs = 'android' | 'ios' | 'web' | string;

export type ApiUrlEnv = {
  expoPublicApiUrl?: string;
  expoPublicApiHost?: string;
  expoPublicApiPort?: string;
  platformOs: PlatformOs;
};

export function resolveApiUrls(env: ApiUrlEnv): { baseUrl: string; apiUrl: string } {
  const rawUrl = env.expoPublicApiUrl?.trim();
  if (rawUrl) {
    // Allow .../api, .../api/, or .../api/v1 style roots from env.
    const base = rawUrl.replace(/\/api(?:\/v\d+)?\/?$/i, '');
    return { baseUrl: base, apiUrl: `${base}/api/v1` };
  }

  const host =
    env.expoPublicApiHost?.trim() ||
    (env.platformOs === 'android' ? '10.0.2.2' : 'localhost');
  const port = env.expoPublicApiPort?.trim() || '8080';
  const baseUrl = `http://${host}:${port}`;
  return { baseUrl, apiUrl: `${baseUrl}/api/v1` };
}

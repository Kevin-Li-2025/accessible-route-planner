import { resolveApiUrls } from '../services/apiConfig';

describe('resolveApiUrls', () => {
  it('uses EXPO_PUBLIC_API_URL and strips trailing /api', () => {
    const r = resolveApiUrls({
      expoPublicApiUrl: 'http://myhost:9000/api/',
      platformOs: 'web',
    });
    expect(r.baseUrl).toBe('http://myhost:9000');
    expect(r.apiUrl).toBe('http://myhost:9000/api/v1');
  });

  it('strips /api/v1 style suffix from full URL', () => {
    const r = resolveApiUrls({
      expoPublicApiUrl: 'http://x:1/api/v1',
      platformOs: 'ios',
    });
    expect(r.baseUrl).toBe('http://x:1');
    expect(r.apiUrl).toBe('http://x:1/api/v1');
  });

  it('defaults to localhost:8080 on ios', () => {
    const r = resolveApiUrls({ platformOs: 'ios' });
    expect(r.baseUrl).toBe('http://localhost:8080');
    expect(r.apiUrl).toBe('http://localhost:8080/api/v1');
  });

  it('uses 10.0.2.2 on android when host not set', () => {
    const r = resolveApiUrls({ platformOs: 'android' });
    expect(r.baseUrl).toBe('http://10.0.2.2:8080');
  });

  it('respects EXPO_PUBLIC_API_HOST and PORT', () => {
    const r = resolveApiUrls({
      platformOs: 'android',
      expoPublicApiHost: '192.168.0.10',
      expoPublicApiPort: '5005',
    });
    expect(r.baseUrl).toBe('http://192.168.0.10:5005');
    expect(r.apiUrl).toBe('http://192.168.0.10:5005/api/v1');
  });
});

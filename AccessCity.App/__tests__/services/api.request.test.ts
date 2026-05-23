import * as SecureStore from 'expo-secure-store';

describe('api.request', () => {
  const originalFetch = global.fetch;

  beforeEach(() => {
    jest.resetModules();
    (SecureStore.getItemAsync as jest.Mock).mockResolvedValue(null);
    (SecureStore.setItemAsync as jest.Mock).mockResolvedValue(undefined);
    (SecureStore.deleteItemAsync as jest.Mock).mockResolvedValue(undefined);
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('returns parsed JSON when Content-Type is application/json', async () => {
    global.fetch = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      headers: {
        get: (name: string) => (name.toLowerCase() === 'content-type' ? 'application/json' : ''),
      },
      json: async () => ({ ok: true, value: 42 }),
      text: async () => '',
    }) as unknown as typeof fetch;

    const { api } = require('@/services/api');
    await expect(api.get('/ping')).resolves.toEqual({ ok: true, value: 42 });
  });

  it('throws with message from error JSON body', async () => {
    global.fetch = jest.fn().mockResolvedValue({
      ok: false,
      status: 400,
      headers: { get: () => '' },
      json: async () => ({}),
      text: async () => JSON.stringify({ message: 'Invalid request' }),
    }) as unknown as typeof fetch;

    const { api } = require('@/services/api');
    await expect(api.get('/bad')).rejects.toThrow('Invalid request');
  });

  it('sends PATCH requests with JSON bodies', async () => {
    global.fetch = jest.fn().mockResolvedValue({
      ok: true,
      status: 204,
      headers: { get: () => '' },
      json: async () => ({}),
      text: async () => '',
    }) as unknown as typeof fetch;

    const { api } = require('@/services/api');
    await expect(api.patch('/hazards/h1', 1)).resolves.toEqual({});

    expect(global.fetch).toHaveBeenCalledWith(
      expect.stringContaining('/hazards/h1'),
      expect.objectContaining({
        method: 'PATCH',
        body: '1',
      }),
    );
  });

  it('URL-encodes refresh tokens sent in the query string', async () => {
    const secureStore = require('expo-secure-store');
    secureStore.getItemAsync.mockImplementation(async (key: string) => {
      if (key === 'ac_refresh_token') return 'abc+/= token';
      return null;
    });
    global.fetch = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      headers: { get: () => 'application/json' },
      json: async () => ({ token: 'new-access', refreshToken: 'new-refresh' }),
      text: async () => '',
    }) as unknown as typeof fetch;

    const { api } = require('@/services/api');
    await expect(api.refreshToken()).resolves.toBe(true);

    expect(global.fetch).toHaveBeenCalledWith(
      expect.stringContaining('token=abc%2B%2F%3D%20token'),
      expect.objectContaining({ method: 'POST' }),
    );
  });
});

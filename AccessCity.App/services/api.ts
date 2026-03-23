import { Platform } from 'react-native';
import { resolveApiUrls } from './apiConfig';
import {
  TOKEN_KEY,
  REFRESH_TOKEN_KEY,
  USER_KEY,
  deleteItemAsync,
  getItemAsync,
  setItemAsync,
} from './sessionStorage';

const { apiUrl: API_URL, baseUrl: API_BASE_URL } = resolveApiUrls({
  expoPublicApiUrl: process.env.EXPO_PUBLIC_API_URL,
  expoPublicApiHost: process.env.EXPO_PUBLIC_API_HOST,
  expoPublicApiPort: process.env.EXPO_PUBLIC_API_PORT,
  platformOs: Platform.OS,
});

export { API_URL, API_BASE_URL };

interface RequestOptions extends RequestInit {
  skipAuth?: boolean;
  _retry?: boolean;
}

type RefreshResponse = {
  token?: string;
  accessToken?: string;
  refreshToken?: string;
};

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

async function clearStoredSession() {
  await deleteItemAsync(TOKEN_KEY);
  await deleteItemAsync(REFRESH_TOKEN_KEY);
  await deleteItemAsync(USER_KEY);
}

export const api = {
  async request<T>(endpoint: string, options: RequestOptions = {}): Promise<T> {
    const url = `${API_URL}${endpoint}`;
    const headers = new Headers(options.headers || {});

    if (!headers.has('Content-Type')) {
      headers.set('Content-Type', 'application/json');
    }

    if (!options.skipAuth) {
      const token = await getItemAsync(TOKEN_KEY);
      console.log('REQUEST TOKEN:', token);

      if (isNonEmptyString(token)) {
        headers.set('Authorization', `Bearer ${token}`);
      }
    } else {
      console.log('REQUEST SKIPS AUTH:', url);
    }

    console.log('REQUEST URL:', url);
    console.log('REQUEST METHOD:', options.method || 'GET');

    const response = await fetch(url, { ...options, headers });

    console.log('RESPONSE STATUS:', response.status, 'URL:', url);

    if (response.status === 401 && !options.skipAuth && !options._retry) {
      console.log('401 DETECTED - TRYING REFRESH TOKEN');

      const refreshed = await this.refreshToken();

      console.log('REFRESH STATUS:', refreshed);

      if (refreshed) {
        console.log('RETRYING ORIGINAL REQUEST:', endpoint);

        return this.request<T>(endpoint, {
          ...options,
          _retry: true,
        });
      }

      await clearStoredSession();
      throw new Error('Session expired. Please log in again.');
    }

    if (!response.ok) {
      let message = `Error: ${response.status}`;
      const text = await response.text().catch(() => '');

      console.log('ERROR RESPONSE TEXT:', text);

      if (text) {
        try {
          const errorData = JSON.parse(text);

          if (Array.isArray(errorData)) {
            message = errorData.map((e: any) => e.description ?? String(e)).join('\n');
          } else if (errorData.errors) {
            const errorList = Object.values(errorData.errors).flat() as string[];
            message = errorList.join('\n');
          }
          else if (errorData.error) {
            message = errorData.hint
              ? `${errorData.error}\n\n${errorData.hint}`
              : errorData.error;
          }
          else if (errorData.message || errorData.title) {
            message = errorData.message || errorData.title;
          } else if (typeof errorData === 'string') {
            message = errorData;
          }
        } catch {
          message = text;
        }
      }

      throw new Error(message);
    }

    const contentType = response.headers.get('Content-Type') || '';

    if (contentType.includes('application/json')) {
      return response.json() as Promise<T>;
    }

    return {} as T;
  },

  async post<T>(endpoint: string, data: any, options: RequestOptions = {}): Promise<T> {
    return this.request<T>(endpoint, {
      ...options,
      method: 'POST',
      body: JSON.stringify(data),
    });
  },

  async get<T>(endpoint: string, options: RequestOptions = {}): Promise<T> {
    return this.request<T>(endpoint, {
      ...options,
      method: 'GET',
    });
  },

  async put<T>(endpoint: string, data: any, options: RequestOptions = {}): Promise<T> {
    return this.request<T>(endpoint, {
      ...options,
      method: 'PUT',
      body: JSON.stringify(data),
    });
  },

  async delete<T>(endpoint: string, options: RequestOptions = {}): Promise<T> {
    return this.request<T>(endpoint, {
      ...options,
      method: 'DELETE',
    });
  },
  async refreshToken(): Promise<boolean> {
  try {
    const storedRefreshToken = await getItemAsync(REFRESH_TOKEN_KEY);
    console.log('REFRESH TOKEN FROM STORE:', storedRefreshToken);

    if (!storedRefreshToken || storedRefreshToken.trim() === '') {
      console.log('NO VALID REFRESH TOKEN FOUND');
      await deleteItemAsync(TOKEN_KEY);
      await deleteItemAsync(REFRESH_TOKEN_KEY);
      return false;
    }

    // 先试 query，而且不要再 encode
    const refreshUrl = `${API_URL}/auth/refresh-token?token=${storedRefreshToken}`;
    console.log('REFRESH REQUEST URL:', refreshUrl);

    let response = await fetch(refreshUrl, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
    });

    console.log('REFRESH RESPONSE STATUS:', response.status);

    if (!response.ok) {
      const firstErrorText = await response.text().catch(() => '');
      console.log('REFRESH ERROR TEXT (QUERY MODE):', firstErrorText);

      console.log('TRYING REFRESH AGAIN WITH JSON BODY');

      response = await fetch(`${API_URL}/auth/refresh-token`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          token: storedRefreshToken,
        }),
      });

      console.log('REFRESH RESPONSE STATUS (BODY MODE):', response.status);
    }

    if (!response.ok) {
      const errorText = await response.text().catch(() => '');
      console.log('REFRESH ERROR TEXT:', errorText);

      await deleteItemAsync(TOKEN_KEY);
      await deleteItemAsync(REFRESH_TOKEN_KEY);
      return false;
    }

    const data = await response.json();
    console.log('REFRESH RESPONSE DATA:', data);

    if (!data.token || !data.refreshToken) {
      console.log('REFRESH RESPONSE MISSING TOKEN FIELDS');
      await deleteItemAsync(TOKEN_KEY);
      await deleteItemAsync(REFRESH_TOKEN_KEY);
      return false;
    }

    await setItemAsync(TOKEN_KEY, data.token);
    await setItemAsync(REFRESH_TOKEN_KEY, data.refreshToken);

    console.log('TOKEN REFRESH SUCCESS');
    return true;
  } catch (e) {
    console.error('TOKEN REFRESH FAILED:', e);
    await deleteItemAsync(TOKEN_KEY);
    await deleteItemAsync(REFRESH_TOKEN_KEY);
    return false;
  }
}
};
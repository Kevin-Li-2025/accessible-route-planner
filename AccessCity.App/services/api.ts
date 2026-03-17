import { Platform } from 'react-native';
import * as SecureStore from 'expo-secure-store';

const BASE_IP = Platform.OS === 'android' ? '10.0.2.2' : 'localhost';
export const API_URL = process.env.EXPO_PUBLIC_API_URL || `http://${BASE_IP}:8080/api`;

const TOKEN_KEY = 'ac_access_token';
const REFRESH_TOKEN_KEY = 'ac_refresh_token';

interface RequestOptions extends RequestInit {
  skipAuth?: boolean;
}

export const api = {
  async request<T>(endpoint: string, options: RequestOptions = {}): Promise<T> {
    const url = `${API_URL}${endpoint}`;
    const headers = new Headers(options.headers || {});
    
    if (!headers.has('Content-Type')) {
      headers.set('Content-Type', 'application/json');
    }

    // Attach token if not skipped
    if (!options.skipAuth) {
      const token = await SecureStore.getItemAsync(TOKEN_KEY);
      if (token) {
        headers.set('Authorization', `Bearer ${token}`);
      }
    }

    const response = await fetch(url, { ...options, headers });

    // Handle Token Expiry
    if (response.status === 401 && !options.skipAuth) {
      const refreshed = await this.refreshToken();
      if (refreshed) {
        // Retry original request with new token
        return this.request(endpoint, options);
      }
      // If refresh fails, let the error bubble up (AuthContext will handle logout)
    }

    if (!response.ok) {
      let message = `Error: ${response.status}`;
      const text = await response.text().catch(() => '');
      
      if (text) {
        try {
          const errorData = JSON.parse(text);
          
          // Handle ASP.NET Identity Errors (Array of {code, description})
          if (Array.isArray(errorData)) {
            message = errorData.map(e => e.description).join('\n');
          } 
          // Handle ASP.NET Validation Errors (Dictionary of errors)
          else if (errorData.errors) {
            const errorList = Object.values(errorData.errors).flat();
            message = errorList.join('\n');
          }
          else if (errorData.message || errorData.title) {
            message = errorData.message || errorData.title;
          }
          else if (typeof errorData === 'string') {
            message = errorData;
          }
        } catch (e) {
          // Not JSON, use the raw text
          message = text;
        }
      }
      
      throw new Error(message);
    }

    return response.json();
  },

  async post<T>(endpoint: string, data: any, options: RequestOptions = {}): Promise<T> {
    return this.request<T>(endpoint, {
      ...options,
      method: 'POST',
      body: JSON.stringify(data),
    });
  },

  async get<T>(endpoint: string, options: RequestOptions = {}): Promise<T> {
    return this.request<T>(endpoint, { ...options, method: 'GET' });
  },

  async refreshToken(): Promise<boolean> {
    try {
      const refreshToken = await SecureStore.getItemAsync(REFRESH_TOKEN_KEY);
      if (!refreshToken) return false;

      // Call refresh endpoint
      const response = await fetch(`${API_URL}/auth/refresh-token?token=${refreshToken}`, {
        method: 'POST'
      });

      if (!response.ok) throw new Error('Refresh failed');

      const data = await response.json();
      
      // Save new tokens
      await SecureStore.setItemAsync(TOKEN_KEY, data.token);
      await SecureStore.setItemAsync(REFRESH_TOKEN_KEY, data.refreshToken);
      
      return true;
    } catch (e) {
      console.error('Token refresh failed', e);
      return false;
    }
  }
};

import * as SecureStore from 'expo-secure-store';
import { api } from './api';
import {
  LoginRequest,
  RegisterRequest,
  AuthResponse,
  ResetPasswordRequest,
} from '../models/auth';

const TOKEN_KEY = 'ac_access_token';
const REFRESH_TOKEN_KEY = 'ac_refresh_token';
const USER_KEY = 'ac_user_data';

type StoredUser = {
  email?: string;
  fullName?: string;
};

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

export const authService = {
  async login(request: LoginRequest): Promise<AuthResponse> {
    const response = await api.post<AuthResponse>('/auth/login', request, {
      skipAuth: true,
    });

    console.log('LOGIN RESPONSE:', response);

    await this.saveSession(response);
    return response;
  },

  async register(request: RegisterRequest): Promise<AuthResponse> {
    const response = await api.post<AuthResponse>('/auth/register', request, {
      skipAuth: true,
    });

    console.log('REGISTER RESPONSE:', response);

    await this.saveSession(response);
    return response;
  },

  async saveSession(data: AuthResponse) {
    console.log('SAVE SESSION INPUT:', data);

    if (!isNonEmptyString(data?.token)) {
      throw new Error('Missing access token in auth response');
    }

    if (!isNonEmptyString(data?.refreshToken)) {
      throw new Error('Missing refresh token in auth response');
    }

    await SecureStore.setItemAsync(TOKEN_KEY, data.token);
    await SecureStore.setItemAsync(REFRESH_TOKEN_KEY, data.refreshToken);

    const userData: StoredUser = {
      email: data.email,
      fullName: data.fullName,
    };

    await SecureStore.setItemAsync(USER_KEY, JSON.stringify(userData));

    const savedToken = await SecureStore.getItemAsync(TOKEN_KEY);
    const savedRefreshToken = await SecureStore.getItemAsync(REFRESH_TOKEN_KEY);
    const savedUser = await SecureStore.getItemAsync(USER_KEY);

    console.log('SAVED TOKEN:', savedToken);
    console.log('SAVED REFRESH TOKEN:', savedRefreshToken);
    console.log('SAVED USER:', savedUser);
  },

  async getSession() {
    const token = await SecureStore.getItemAsync(TOKEN_KEY);
    const refreshToken = await SecureStore.getItemAsync(REFRESH_TOKEN_KEY);
    const userJson = await SecureStore.getItemAsync(USER_KEY);

    console.log('GET SESSION TOKEN:', token);
    console.log('GET SESSION REFRESH TOKEN:', refreshToken);
    console.log('GET SESSION USER:', userJson);

    if (!isNonEmptyString(token)) {
      return null;
    }

    if (!userJson) {
      return null;
    }

    try {
      const user = JSON.parse(userJson);

      return {
        token,
        refreshToken: isNonEmptyString(refreshToken) ? refreshToken : null,
        user,
      };
    } catch (error) {
      console.warn('FAILED TO PARSE USER SESSION:', error);
      return null;
    }
  },

  async clearSession() {
    await SecureStore.deleteItemAsync(TOKEN_KEY);
    await SecureStore.deleteItemAsync(REFRESH_TOKEN_KEY);
    await SecureStore.deleteItemAsync(USER_KEY);

    console.log('SESSION CLEARED');
  },

  async logout() {
    const refreshToken = await SecureStore.getItemAsync(REFRESH_TOKEN_KEY);

    console.log('LOGOUT REFRESH TOKEN:', refreshToken);

    if (isNonEmptyString(refreshToken)) {
      try {
        await api.request(`/auth/revoke-token?token=${encodeURIComponent(refreshToken)}`, {
          method: 'POST',
          skipAuth: true,
        });
        console.log('BACKEND LOGOUT SUCCESS');
      } catch (e) {
        console.warn('BACKEND LOGOUT FAILED:', e);
      }
    }

    await this.clearSession();
  },

  async forgotPassword(email: string): Promise<{ message: string }> {
    console.log('FORGOT PASSWORD EMAIL:', email);

    return api.post<{ message: string }>(
      '/auth/forgot-password',
      { email },
      { skipAuth: true }
    );
  },

  async resetPassword(
    request: ResetPasswordRequest
  ): Promise<{ message: string }> {
    console.log('RESET PASSWORD REQUEST:', request);

    return api.post<{ message: string }>(
      '/auth/reset-password',
      request,
      { skipAuth: true }
    );
  },
};
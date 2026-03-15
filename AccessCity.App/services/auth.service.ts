import * as SecureStore from 'expo-secure-store';
import { api } from './api';
import { LoginRequest, RegisterRequest, AuthResponse } from '../models/auth';

const TOKEN_KEY = 'ac_access_token';
const REFRESH_TOKEN_KEY = 'ac_refresh_token';
const USER_KEY = 'ac_user_data';

export const authService = {
  async login(request: LoginRequest): Promise<AuthResponse> {
    const response = await api.post<AuthResponse>('/auth/login', request, { skipAuth: true });
    await this.saveSession(response);
    return response;
  },

  async register(request: RegisterRequest): Promise<AuthResponse> {
    const response = await api.post<AuthResponse>('/auth/register', request, { skipAuth: true });
    await this.saveSession(response);
    return response;
  },

  async saveSession(data: AuthResponse) {
    await SecureStore.setItemAsync(TOKEN_KEY, data.token);
    await SecureStore.setItemAsync(REFRESH_TOKEN_KEY, data.refreshToken);
    await SecureStore.setItemAsync(USER_KEY, JSON.stringify({
      email: data.email,
      fullName: data.fullName
    }));
  },

  async getSession() {
    const token = await SecureStore.getItemAsync(TOKEN_KEY);
    const userJson = await SecureStore.getItemAsync(USER_KEY);
    
    if (token && userJson) {
      return {
        token,
        user: JSON.parse(userJson)
      };
    }
    return null;
  },

  async logout() {
    const refreshToken = await SecureStore.getItemAsync(REFRESH_TOKEN_KEY);
    if (refreshToken) {
      try {
        await api.request(`/auth/revoke-token?token=${refreshToken}`, { 
          method: 'POST',
          skipAuth: true 
        });
      } catch (e) {
        console.warn('Backend logout failed', e);
      }
    }
    await SecureStore.deleteItemAsync(TOKEN_KEY);
    await SecureStore.deleteItemAsync(REFRESH_TOKEN_KEY);
    await SecureStore.deleteItemAsync(USER_KEY);
  },

  async forgotPassword(email: string): Promise<{ message: string }> {
    return api.post<{ message: string }>('/auth/forgot-password', { email }, { skipAuth: true });
  },

  async resetPassword(request: ResetPasswordRequest): Promise<{ message: string }> {
    return api.post<{ message: string }>('/auth/reset-password', request, { skipAuth: true });
  }
};

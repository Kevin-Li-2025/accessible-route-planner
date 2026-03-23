import { Platform } from 'react-native';
import * as SecureStore from 'expo-secure-store';

export const TOKEN_KEY = 'ac_access_token';
export const REFRESH_TOKEN_KEY = 'ac_refresh_token';
export const USER_KEY = 'ac_user_data';

const isWeb = Platform.OS === 'web';

/**
 * expo-secure-store on web ships an empty native stub, so setItemAsync calls
 * setValueWithKeyAsync on undefined. Use localStorage on web for auth/session keys.
 */
export async function setItemAsync(
  key: string,
  value: string,
  options?: SecureStore.SecureStoreOptions
): Promise<void> {
  if (isWeb) {
    if (typeof localStorage === 'undefined') {
      throw new Error('localStorage is not available');
    }
    localStorage.setItem(key, value);
    return;
  }
  await SecureStore.setItemAsync(key, value, options);
}

export async function getItemAsync(
  key: string,
  options?: SecureStore.SecureStoreOptions
): Promise<string | null> {
  if (isWeb) {
    if (typeof localStorage === 'undefined') {
      return null;
    }
    return localStorage.getItem(key);
  }
  return SecureStore.getItemAsync(key, options);
}

export async function deleteItemAsync(
  key: string,
  options?: SecureStore.SecureStoreOptions
): Promise<void> {
  if (isWeb) {
    if (typeof localStorage !== 'undefined') {
      localStorage.removeItem(key);
    }
    return;
  }
  await SecureStore.deleteItemAsync(key, options);
}
